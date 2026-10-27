# DuoTrack

通过自己的公网服务器，让一台 Windows 电脑的键盘和鼠标控制另一台电脑。

## 用户体验

- 客户端是窗口和系统托盘程序，不需要命令行
- 每台电脑首次启动自动生成设备身份，私钥由 Windows 当前用户加密保护
- 用户不需要填写或保存共享密钥
- 一台电脑生成 6 位一次性配对码，另一台输入即可配对
- 配对完成后，从在线设备列表中点击“控制”
- `Ctrl+Alt+F12` 可快速开始或停止控制
- 支持开机自动启动、自动重连和断线释放按键

当前仅支持 Windows 客户端。普通权限程序不能控制 UAC 安全桌面、登录界面或权限更高的程序。

## 一次性部署服务器

要求：

- 一台安装了 Docker Engine 和 Docker Compose 的 Linux 服务器
- 一个解析到服务器公网 IP 的域名
- 公网开放 TCP 80、TCP/UDP 443

上传本目录到服务器，然后执行：

```bash
cd DuoTrack
chmod +x install.sh
./install.sh duotrack.example.com
```

安装脚本会保存域名、构建中继服务并启动内置 Caddy。Caddy 自动申请和续期 HTTPS 证书；SQLite 数据库、设备身份和配对关系存放在 Docker 数据卷中。

验证：

```bash
curl https://duotrack.example.com/health
docker compose ps
docker compose logs --tail=100
```

正常应返回：

```json
{"status":"ok"}
```

以后更新服务端：

```bash
docker compose up -d --build
```

## 客户端使用

1. 在两台电脑运行 `DuoTrack.Client.exe`。
2. 填写同一个服务器地址，例如 `https://duotrack.example.com`，设置电脑名称，点击“保存并连接”。地址只需填写一次。
3. 在第一台点击“生成配对码”。
4. 在第二台输入这组 6 位配对码并点击“使用配对码连接”。
5. 刷新设备列表，选择在线设备并点击“控制”。
6. 按 `Ctrl+Alt+F12` 返回本机；再次按下可继续控制当前选择的设备。

第一次测试时，建议在被控制电脑旁保留备用键鼠。

## 编译与发布

需要 .NET 8 SDK 或更高版本：

```powershell
dotnet build .\src\DuoTrack.Relay\DuoTrack.Relay.csproj -c Release
dotnet build .\src\DuoTrack.Client\DuoTrack.Client.csproj -c Release

dotnet publish .\src\DuoTrack.Client\DuoTrack.Client.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -o .\publish\win-x64-single
```

本机启动开发服务器：

```powershell
dotnet run --project .\src\DuoTrack.Relay\DuoTrack.Relay.csproj `
  --urls http://127.0.0.1:18080
```

客户端服务器地址填写 `http://127.0.0.1:18080`。

运行完整服务端集成测试：

```powershell
.\scripts\integration-test.ps1
```

## 安全说明

- 设备通过 ECDSA P-256 签名挑战进行认证，不使用所有设备共享的 Token。
- 配对码 5 分钟失效、使用一次即删除，并限制尝试频率。
- 服务端只向已经配对的设备转发输入。
- 公网连接通过 Caddy 使用 HTTPS/WSS。
- 当前输入载荷会在自己的服务器上解密后转发，尚未实现设备到设备的端到端加密。因此目前仅适合部署在自己信任的服务器上。

## 后续阶段

1. 输入载荷端到端加密和配对双方确认
2. 鼠标移动合并与弱网优化
3. 屏幕布局和边缘自动切换
4. 剪贴板同步
5. 安装包、代码签名和自动更新
