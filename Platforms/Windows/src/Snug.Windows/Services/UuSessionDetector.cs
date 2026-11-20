using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Snug.Windows.Core;
using Snug.Windows.Interop;

namespace Snug.Windows.Services;

/// <summary>
/// Reads the UU overlay, scoped controller table, active peer TCP and bounded streamer-log hints.
/// The SYSTEM startup monitor overrides sticky hints only with a fresh,
/// verified-generation, complete capture inventory.
/// </summary>
public sealed class UuSessionDetector : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string DefaultLogDirectory = @"D:\GameViewer\log\server\log";

    private readonly object _gate = new();
    private readonly UuControllerReader _controllerReader = new();
    private readonly UuCaptureMonitorReader? _captureMonitor = UuCaptureMonitorReader.FromEnvironment();
    private readonly UuStreamLogActivityTracker _streamLogActivity = new();
    private readonly UuStreamLogActivityTracker _connectionLogActivity = new();
    private UuServerGeneration? _serverGeneration;
    private string _logSummary = "等待 UU 会话日志";
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
    private bool _polling;
    private UuSignals _cached = new(false, null, false);

    public string LocalControllerSummary { get; private set; } = "未读取";
    public string LastSignalSummary { get; private set; } = "等待 UU 会话日志";
    public int? ServerProcessId { get; private set; }

    public UuSignals Sample()
    {
        var local = _controllerReader.Sample();
        LocalControllerSummary = local.Status;
        lock (_gate)
        {
            if (!_polling && DateTimeOffset.UtcNow - _lastPoll >= PollInterval)
            {
                _polling = true;
                _ = Task.Run(Poll);
            }
            var current = _cached with { Controllers = local.Controllers };
            if (_captureMonitor is null) return current;
            var generation = DateTimeOffset.UtcNow - _lastPoll <= UuCaptureFeed.Freshness ? _serverGeneration : null;
            var capture = _captureMonitor.Sample(generation);
            LastSignalSummary = capture.Phase switch
            {
                UuCapturePhase.Active => "ETW 捕获中 · 设备与屏幕未知",
                UuCapturePhase.Rebuilding => "ETW 捕获重建中",
                UuCapturePhase.Inactive => "ETW 捕获已停止",
                _ => "ETW 未知 · " + _logSummary
            };
            return UuCaptureLifecycle.Combine(current, capture);
        }
    }

    private void Poll()
    {
        UuSignals signals;
        string summary;
        int? serverPid;
        UuServerGeneration? generation = null;

        try
        {
            var serverProcesses = Process.GetProcessesByName("GameViewerServer");
            try
            {
                var pids = serverProcesses.Select(process => (uint)process.Id).ToHashSet();
                serverPid = pids.Count == 0 ? null : (int)pids.OrderByDescending(value => value).First();
                if (_captureMonitor is not null && serverProcesses.Length == 1)
                    generation = UuServerIdentity.ReadSingle();

                if (pids.Count == 0)
                {
                    _streamLogActivity.Reset();
                    _connectionLogActivity.Reset();
                    signals = new UuSignals(false, null, false);
                    summary = "GameViewerServer 未运行";
                }
                else if (serverProcesses.Length != 1 || serverPid is null)
                {
                    _streamLogActivity.Reset();
                    _connectionLogActivity.Reset();
                    signals = new UuSignals(false, null, null);
                    summary = "GameViewerServer 多进程 · 推流状态未知";
                }
                else
                {
                    var overlayVisible = HasVisibleServerWindow(serverPid.Value);
                    var peerConnection = HasActivePeerTcpConnection(serverPid.Value);
                    var logDirectory = ResolveLogDirectory(serverProcesses);
                    var streamPath = ResolveCurrentStreamerLog(logDirectory, serverPid.Value);
                    var connectionPath = ResolveCurrentConnectionLog(logDirectory, serverPid.Value);
                    var streamActive = false;
                    var streamStatus = streamPath is null ? "missing" : "idle";
                    if (streamPath is not null)
                    {
                        try
                        {
                            var streamFile = new FileInfo(streamPath);
                            streamFile.Refresh();
                            streamActive = _streamLogActivity.Observe(
                                streamPath, streamFile.Length, DateTimeOffset.UtcNow);
                            if (streamActive) streamStatus = "growing";
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            _streamLogActivity.Reset();
                            streamStatus = "unreadable";
                        }
                    }
                    else
                    {
                        _streamLogActivity.Reset();
                    }

                    // The connection log only grows while a remote client session is live;
                    // Snug's own display churn and the server's heartbeat grow the streamer
                    // log but never this one, so its growth alone is reliable session evidence.
                    var connectionActive = false;
                    if (connectionPath is not null)
                    {
                        try
                        {
                            var connectionFile = new FileInfo(connectionPath);
                            connectionFile.Refresh();
                            connectionActive = _connectionLogActivity.Observe(
                                connectionPath, connectionFile.Length, DateTimeOffset.UtcNow);
                            if (connectionActive) streamStatus += "+connection";
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            _connectionLogActivity.Reset();
                        }
                    }
                    else
                    {
                        _connectionLogActivity.Reset();
                    }

                    signals = new UuSignals(
                        OverlayVisible: overlayVisible,
                        Controller: null,
                        CaptureActive: null,
                        LogFallback: connectionActive,
                        PeerConnection: peerConnection);

                    summary = "Server PID " + serverPid
                        + " · overlay " + (overlayVisible ? "visible" : "hidden")
                        + " · peer TCP " + (peerConnection ? "active" : "none")
                        + " · streamer log " + streamStatus;
                }
            }
            finally
            {
                foreach (var process in serverProcesses) process.Dispose();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _streamLogActivity.Reset();
            _connectionLogActivity.Reset();
            signals = new UuSignals(false, null, null);
            summary = "UU 会话日志暂不可读：" + error.GetType().Name;
            serverPid = null;
            generation = null;
        }

        lock (_gate)
        {
            _cached = signals;
            _lastPoll = DateTimeOffset.UtcNow;
            _polling = false;
            _logSummary = summary;
            _serverGeneration = generation;
            if (_captureMonitor is null) LastSignalSummary = summary;
            ServerProcessId = serverPid;
        }
    }

    private static string? ResolveCurrentStreamerLog(string directory, int serverPid)
    {
        if (!Directory.Exists(directory)) return null;
        var pattern = "streamer_log_controlled_*_" + serverPid + ".slog";
        return Directory.EnumerateFiles(directory, pattern)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? ResolveCurrentConnectionLog(string directory, int serverPid)
    {
        if (!Directory.Exists(directory)) return null;
        var pattern = "connection_log_controlled_*_" + serverPid + ".slog";
        return Directory.EnumerateFiles(directory, pattern)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool HasVisibleServerWindow(int serverPid)
    {
        var visible = false;
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsHungAppWindow(handle))
                return true;

            NativeMethods.GetWindowThreadProcessId(handle, out var ownerPid);
            if (ownerPid != (uint)serverPid) return true;
            if (!NativeMethods.GetWindowRect(handle, out var rect)
                || rect.Right - rect.Left <= 10 || rect.Bottom - rect.Top <= 10)
                return true;

            visible = true;
            return false;
        }, IntPtr.Zero);
        return visible;
    }

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint TcpStateEstablished = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool ordered, int addressFamily, int tableClass, uint reserved);

    private static bool HasActivePeerTcpConnection(int serverPid)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0) return false;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
            if (result != 0) return false;

            var count = Marshal.ReadInt32(buffer);
            if (count <= 0 || count > 65536) return false;

            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowPointer = IntPtr.Add(buffer, sizeof(uint));
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                rowPointer = IntPtr.Add(rowPointer, rowSize);
                var remotePort = (int)(((row.RemotePort & 0xFFu) << 8)
                    | ((row.RemotePort >> 8) & 0xFFu));

                if (UuTcpPeerEvidence.IsActivePeer(
                    row.OwningProcessId, (uint)serverPid, row.State, row.RemoteAddress, remotePort))
                    return true;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
    private static string ResolveLogDirectory(IEnumerable<Process> serverProcesses)
    {
        foreach (var process in serverProcesses)
        {
            try
            {
                var executable = process.MainModule?.FileName;
                var binDirectory = Path.GetDirectoryName(executable);
                var appRoot = binDirectory is null ? null : Directory.GetParent(binDirectory)?.FullName;
                if (appRoot is null) continue;

                var candidate = Path.Combine(appRoot, "log", "server", "log");
                if (Directory.Exists(candidate)) return candidate;
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }

        return DefaultLogDirectory;
    }
    public void Dispose() => _captureMonitor?.Dispose();
}
