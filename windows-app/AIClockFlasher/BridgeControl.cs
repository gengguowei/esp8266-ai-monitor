using System.Diagnostics;

namespace AIClockFlasher;

/// AIClockBridge holds the CH340 port open and re-scans for it every three
/// seconds. esptool cannot open a busy port, and worse, the bridge can grab it
/// back in the middle of a write. So it is stopped before touching the device
/// and started again afterwards - the same dance dist\flash-firmware.ps1 does,
/// moved into the tool so nobody has to remember it.
sealed class BridgeControl
{
    const string ProcessName = "AIClockBridge";

    string _restorePath;
    readonly Action<string> _log;

    public BridgeControl(Action<string> log) => _log = log ?? (_ => { });

    public bool WasRunning => _restorePath != null;

    public void Stop()
    {
        var running = Process.GetProcessesByName(ProcessName);
        if (running.Length == 0) return;

        try { _restorePath = running[0].MainModule?.FileName; } catch { _restorePath = null; }

        foreach (var p in running)
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
            finally { p.Dispose(); }
        }
        _log("已停止 AIClockBridge，串口释放中…");
        Thread.Sleep(1200);   // let Windows release the handle
    }

    public void Restart()
    {
        if (_restorePath == null) return;
        if (!File.Exists(_restorePath))
        {
            _log($"AIClockBridge 已停止，但找不到 {_restorePath}，请手动启动。");
            _restorePath = null;
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_restorePath) { UseShellExecute = true });
            _log("AIClockBridge 已重新启动。");
        }
        catch (Exception e)
        {
            _log($"重新启动 AIClockBridge 失败：{e.Message}");
        }
        _restorePath = null;
    }
}
