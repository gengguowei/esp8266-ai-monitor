# AIClockFlasher

固件刷写与回滚的小工具。取代 `dist\setup-firmware-env.cmd` → `flash-step1-dryrun.cmd` →
`flash-step2-write.cmd` 那一串脚本：串口自己找，版本自己认，备份自己做，进度自己显示。

```powershell
cd windows-app\AIClockFlasher; dotnet run                 # 本地跑起来
powershell -File .\windows-app\AIClockFlasher\build-flasher.ps1   # 出 dist\ 单文件 exe
```

## 它做什么

- **自动连接／断开**：按 CH340（1A86:7523）和 CP210x 的 VID/PID 从注册表挑出板子的串口，
  插上就连、拔掉就断。刷写全程自动停掉 `AIClockBridge`（它每 3 秒抢一次串口），完事再拉起来。
- **显示两边的版本号**：设备端趁固件还在跑的时候发一句 `#HELLO`，固件回 `#DEVICE {…,"fw":"0.4.14"}`；
  固件文件那一侧扫描镜像里内嵌的 `Firmware v0.4.14` 字面量（配网界面画的那行字），
  比文件名可靠。两边都有就直接告诉你这是升级、降级还是重复刷。
- **固件信息**：段数、Flash 模式／容量／频率、入口地址、大小、SHA-256。
- **自动备份**：刷写前先把设备现有内容读回来存成 `dist\backups\backup-esp8266-<版本>-<范围>-<时间>.bin`。
  默认只备份应用区 1MB（约 20 秒，够回滚固件版本），也可以整片 4MB（连 LittleFS 里的 WiFi
  配置、亮度、自定义桌宠一起）。
- **恢复**：任何一份备份都能一键写回去，写完照样校验。
- **写入后校验**：让芯片自己算 0x0 起的 MD5，和文件比对，不一致直接报错。
- 写入只覆盖 `0x0` 起的应用区，从不使用 `erase_flash`，所以 WiFi 配置和自定义桌宠都在。

## 为什么不调用 esptool

因为那要求每台机器先装 Python、再 `pip install esptool`、还得躲开公司代理——
`dist\setup-firmware-env.ps1` 里一半的篇幅在处理这件事。`Esp\EspLoader.cs` 直接用
`System.IO.Ports` 实现了 ESP8266 ROM bootloader 的下载协议（SLIP 分帧、SYNC、
`MEM_*`／`FLASH_*`／`READ_FLASH`／`SPI_FLASH_MD5`，以及读 flash ID 用的 SPI 用户命令寄存器），
双击 exe 就能用。

唯一的外来二进制是 `Assets\stub_flasher_8266.json`——乐鑫自己的 ESP8266 flasher stub，
从 esptool-js（Apache-2.0）原样拿来的。它不是可选项：ESP8266 的 ROM **没有**读 Flash 的命令
（`ESP_READ_FLASH_SLOW` 只存在于 ESP32），不跑 stub 就没有「刷写前先备份」这回事。
顺带把写入块从 1KB 提到 16KB。详见 `Assets\STUB-LICENSE.txt`。

## 文件

| | |
|---|---|
| `Esp\EspLoader.cs` | ROM bootloader 协议：SLIP、复位进下载模式、寄存器读写、stub 上传、读写校验 Flash |
| `Esp\EspStub.cs` | 内嵌 stub 的解析与加载 |
| `FirmwareImage.cs` | `.bin` 头解析、版本号提取、哈希、自动发现仓库里的固件 |
| `DeviceProbe.cs` | 用 `#HELLO` 问出设备当前固件版本 |
| `FlashService.cs` | 连接／备份／刷写／恢复的流程编排 |
| `BridgeControl.cs` | 刷写期间停用并恢复 AIClockBridge |
| `SerialPorts.cs` | 串口枚举与友好名（注册表，不引入 System.Management） |
| `MainForm.cs` | 界面 |

## 常见问题

- **进不了下载模式**：多半是只能充电的 USB 线，或者没装 [CH340 驱动](https://www.wch.cn/downloads/CH341SER_EXE.html)。
- **串口被占用**：工具会自动停 AIClockBridge，但串口助手、PlatformIO 监视器需要手动关。
- **校验和错误 (0x07)**：串口不稳，把波特率降到 230400 或 115200 再试。
- **「设备没有回应版本查询」**：空片、刷了别的固件，或者设备正停在配网界面——不影响刷写。
