namespace AIClockFlasher.Esp;

/// Any failure that came from the chip or the wire. Kept separate from
/// ordinary exceptions so the UI can show the message as-is instead of a
/// stack trace: every message here is written to be read by a human who is
/// holding a USB cable.
class EspException : Exception
{
    public EspException(string message) : base(message) { }
    public EspException(string message, Exception inner) : base(message, inner) { }
}

sealed class EspTimeoutException : EspException
{
    public EspTimeoutException(string message) : base(message) { }
}

/// The ROM/stub answered with a non-zero status. The second status byte is a
/// small error code that says a lot more than "command failed".
sealed class EspCommandException : EspException
{
    public byte Code { get; }

    public EspCommandException(string what, byte code)
        : base($"{what} 失败：{Describe(code)} (0x{code:X2})")
    {
        Code = code;
    }

    static string Describe(byte code) => code switch
    {
        0x05 => "收到无法识别的命令",
        0x06 => "命令执行失败",
        0x07 => "数据包校验和错误（串口不稳定，试试降低波特率或换数据线）",
        0x08 => "Flash 写入错误",
        0x09 => "Flash 读取错误",
        0x0A => "Flash 读取长度错误",
        0x0B => "解压错误",
        _ => "未知错误",
    };
}
