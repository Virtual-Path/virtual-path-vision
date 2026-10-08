<p align="center">
  <a href="README.md">English</a> | <a href="README.zh-CN.md">中文</a>
</p>

<h1 align="center">Virtual Path Vision</h1>

<p align="center">
  <img src="https://img.shields.io/github/v/release/virtual-path/virtual-path-vision?style=flat-square&label=release" alt="release"/>
  <img src="https://img.shields.io/github/stars/virtual-path/virtual-path-vision?style=flat-square" alt="stars"/>
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=.net" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/WPF-Light%20%2F%20Dark%20UI-58A6FF?style=flat-square" alt="WPF"/>
  <img src="https://img.shields.io/badge/OpenCV-5-5C3EE8?style=flat-square&logo=opencv" alt="OpenCV 5"/>
  <img src="https://img.shields.io/badge/AWS-S3%20%C2%B7%20IoT%20%C2%B7%20Lambda-FF9900?style=flat-square&logo=amazonaws" alt="AWS"/>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4?style=flat-square" alt="Windows"/>
  <img src="https://img.shields.io/badge/lang-EN%20%2F%20中文-3FB950?style=flat-square" alt="i18n"/>
  <img src="https://img.shields.io/badge/license-GPL--3.0-blue?style=flat-square" alt="License"/>
</p>

<p align="center">
  一款现代机器视觉桌面应用，支持本地/网络摄像头的实时视频处理、
  图像识别、条码扫描与测量。
  <br/>
  基于 <strong>WPF</strong> · <strong>OpenCvSharp5 (OpenCV 5)</strong> · <strong>AWS SDK</strong> · <strong>.NET 8</strong> 构建
</p>

<p align="center">
  <img src="docs/images/01_ui.png" alt="深色主题" width="432"/>
  <img src="docs/images/01_ui_light.png" alt="浅色主题" width="432"/>
</p>

---

## 功能特性

- **双信号源** – 本地 USB 摄像头或网络 IP 摄像头（RTSP / MJPEG over HTTP）
- **人脸检测** – OpenCV 5 DNN 检测器（YuNet ONNX），绘制检测框、5 个面部关键点与置信度
- **11 种处理模式** – Canny、Sobel、Laplacian、二值化、轮廓、QR/条码、颜色检测、模板匹配、形状识别、特征点匹配、图像增强
- **QR / 条码** – 实时解码二维码与一维条码（EAN/UPC/Code128/Code39），画面直接显示识别结果
- **颜色检测** – 基于 HSV 的 9 种预设颜色检测与目标计数，支持点击画面直接取色
- **模板匹配** – 加载模板图像，在实时画面中定位并显示匹配分数
- **形状识别** – 自动分类圆形、矩形、三角形、五边形、多边形，并输出分类统计
- **特征点匹配** – ORB 特征点 + RANSAC 单应矩阵，抗旋转、缩放变化
- **图像增强** – CLAHE 直方图均衡 + 非锐化掩模，改善低对比度画面
- **视频录像** – 一键将处理后的视频录制为 AVI 文件
- **截图** – 将当前帧保存为 PNG 图片
- **网络摄像头** – 通过 IP Webcam 类应用连接手机摄像头
- **双语支持** – 内置中文与英文，运行时一键切换
- **明暗双主题** – Apple 风格卡片界面，运行时切换主题（含"跟随系统"）与可折叠侧边栏
- **AI 主动感知** – YOLO 目标检测、卡尔曼多目标跟踪与数字孪生叠加（需自行提供 `.onnx` 模型，仓库不含模型）
- **AWS 云服务** – S3 截图上传、IoT Core 遥测、Lambda 调用
- **工业互联** – Modbus TCP、OPC UA、串口 / TCP 条码枪与报工导出
- **自适应布局** – 窄窗口自动堆叠；2K/4K 屏清晰（PerMonitorV2 DPI）
- **图片分析** – 加载静态图片并应用完整处理流水线
- **实时统计** – FPS、处理耗时、人脸/轮廓计数

---

## 处理模式效果

以下效果图均由应用自身使用内置测试图 `TestImages/test_scene.png` 生成。

<table>
  <tr>
    <td align="center"><b>Canny 边缘检测</b><br/><img src="docs/images/02_canny.png" width="360"/></td>
    <td align="center"><b>轮廓检测</b><br/><img src="docs/images/03_contour.png" width="360"/></td>
  </tr>
  <tr>
    <td align="center"><b>二值化</b><br/><img src="docs/images/04_binary.png" width="360"/></td>
    <td align="center"><b>形状识别</b><br/><img src="docs/images/05_shape.png" width="360"/></td>
  </tr>
  <tr>
    <td align="center"><b>颜色检测（红色）</b><br/><img src="docs/images/06_color.png" width="360"/></td>
    <td align="center"><b>QR / 条码识别</b><br/><img src="docs/images/07_qr.png" width="360"/></td>
  </tr>
  <tr>
    <td align="center"><b>模板匹配</b><br/><img src="docs/images/08_template.png" width="360"/></td>
    <td align="center"><b>特征点匹配（ORB）</b><br/><img src="docs/images/09_feature.png" width="360"/></td>
  </tr>
  <tr>
    <td align="center"><b>图像增强（CLAHE + 锐化）</b><br/><img src="docs/images/10_enhance.png" width="360"/></td>
    <td align="center"><b>人脸检测</b><br/><img src="docs/images/11_face.png" width="360"/></td>
  </tr>
</table>

---

## 使用流程

1. **选择信号源** – 选择"本地摄像头"、"网络视频流"或"录像回放"
2. **配置** – 网络模式输入 IP 地址和端口；回放模式点"浏览…"选择录像文件
3. **连接** – 建立视频流连接或开始回放
4. **选择模式** – 从 11 种处理算法中选择一种
5. **处理** – 实时处理 + 人脸检测，实时显示 FPS 和处理耗时
6. **调整** – 微调 Canny 阈值并应用（Canny / 轮廓模式）
7. **扫描 / 检测 / 匹配** – 将摄像头对准二维码、彩色物体、形状或已加载的模板
8. **保存** – 随时截图或录制视频
9. **加载图片** – 从图片文件离线分析

所有处理均在后台线程异步执行，UI 始终保持流畅。

---

## 使用手机作为摄像头

1. 在手机上安装 **IP Webcam**（Android）或同类应用
2. 确保手机与电脑在同一 Wi-Fi 网络
3. 启动应用并记录 URL（如 `http://192.168.1.100:8080/video`）
4. 在本应用中：
   - 信号源选择 **"网络视频流"**
   - 输入 IP 和端口
   - 点击 **连接**

应用会自动拼接 MJPEG URL 并开始拉流。

---

## 使用录像回放调参

产线缺陷往往难以复现，先跑录像回放最容易定位阈值问题。

1. 信号源选择 **"录像回放"**
2. 点 **"浏览…"** 选择录像文件（支持 mp4 / avi / mkv / mov / wmv / m4v，也支持图像序列目录）
3. 按需设置：
   - **FPS** – 回放节流帧率。留空则采用录像自带的帧率。设为 0 表示不节流（会以最快速度播完，通常不需要）
   - **循环** – 勾选后播到末尾自动从头继续
4. 点 **连接**，面板右侧显示 `当前帧 / 总帧数` 与进度条

回放与实时采集走同一套处理链路，因此检测模式、阈值、录制、截图等行为完全一致。

> 回放模式不需要任何外部服务。若要复现"3D 引擎当虚拟相机"的场景，
> 改用"网络视频流"，地址填 `127.0.0.1`、端口填引擎服务的端口（如 8080）。

---

## 项目结构

```
├── VirtualPathVision/               # 应用工程
│   ├── App.xaml / App.xaml.cs       # 应用入口、DI 容器、全局异常兜底
│   ├── MainWindow.xaml / .cs        # 主界面、导航与事件编排
│   ├── SettingsWindow.xaml / .cs    # 设置窗口（语言 + 主题）
│   ├── ThemeService.cs              # 明/暗/跟随系统 主题切换
│   ├── TranslationService.cs        # i18n 单例服务（INotifyPropertyChanged）
│   ├── UserSettings.cs              # user_settings.json 的加锁 + 原子读写
│   ├── AppConfig.cs                 # AI / AWS 配置段的强类型绑定
│   ├── AppLogger.cs                 # 日志服务（单例，封送到 UI 线程）
│   ├── app.manifest                 # PerMonitorV2 DPI 感知
│   ├── appsettings.json             # 工业互联 / AI / AWS 参数（启动时读取一次）
│   ├── Resources/
│   │   ├── Strings.resx             # 中文资源（回退语言）
│   │   └── Strings.en.resx          # 英文资源
│   ├── Themes/
│   │   ├── LightTheme.xaml          # Apple 风格浅色配色 + 控件样式
│   │   └── DarkTheme.xaml           # Apple 风格深色配色 + 控件样式
│   ├── Views/                       # 每页一个 UserControl
│   │   ├── CameraPanel.xaml / .cs   # 首页：采集、预览、截图/录像
│   │   ├── ProcessingPanel.xaml / .cs   # 11 种处理模式 + 阈值
│   │   ├── AIPanel.xaml / .cs       # YOLO 检测、跟踪、数字孪生
│   │   ├── CloudPanel.xaml / .cs    # AWS S3 / IoT Core / Lambda
│   │   ├── IndustrialPanel.xaml / .cs   # Modbus / OPC UA / 条码枪 / 报工
│   │   └── LogPanel.xaml / .cs      # 应用日志
│   ├── AI/                          # 主动感知、卡尔曼跟踪、数字孪生、缺陷判定
│   ├── Cloud/                       # S3Service、IoTService、LambdaClient
│   ├── Industrial/                  # Modbus、OPC UA、串口/TCP 条码枪、报工存储
│   ├── Components/                  # 采集 + 图像处理组件
│   ├── Converters/                  # 值转换器（日志级别 → 颜色 等）
│   ├── face_detection_yunet_2023mar.onnx
│   └── haarcascade_frontalface_default.xml
├── TestImages/                      # 测试图片（场景、模板、人脸照片）
└── docs/images/                     # README 使用的截图
```

> **说明：** `haarcascade_frontalface_default.xml` 已不再使用——人脸检测完全基于
> YuNet DNN 模型，该文件仅作参考保留。

### 核心架构

| 组件 | 职责 |
|------|------|
| `VideoCaptureComponent` | `LocalCamera` / `NetworkStream` 双信号源，自动降级尝试 API（DSHOW → MSMF → ANY），连接状态机 |
| `ImageDisplayComponent` | 批量 `Dispatcher.Invoke` 双图更新 |
| `ImageProcessingComponent` | 5 种经典模式：Canny、Sobel、Laplacian、二值化、轮廓检测 |
| `FaceDetectionComponent` | OpenCV 5 `FaceDetectorYN`（YuNet ONNX）——检测框 + 5 个关键点 + 置信度；模型缺失时优雅降级为"不可用" |
| `BarcodeDetectionComponent` | ZXing.Net 解码 QR/DataMatrix/EAN/UPC/Code128/Code39，帧节流 + 结果缓存（单调时钟计时） |
| `ColorDetectionComponent` | HSV `InRange` 掩码 + 形态学 + 轮廓计数，9 种预设色 + 点击取色（支持跨色环接缝） |
| `TemplateMatchComponent` | `MatchTemplate`（CCoeffNormed）+ 阈值过滤 + 分数叠加，模板读写加锁 |
| `ShapeDetectionComponent` | Canny + 多边形逼近 + 圆形度分析，分类圆形/矩形/三角形/五边形/多边形；共用主界面的 Canny 阈值滑块 |
| `FeatureMatchComponent` | ORB 特征点 + BFMatcher 比率测试 + RANSAC 单应矩阵，绘制透视定位框 |
| `EnhancementComponent` | CLAHE 直方图均衡 + 非锐化掩模 |
| `RecordingComponent` | `VideoWriter` AVI 录制（MJPG 编码，帧率取自信号源实际值） |
| `ThresholdParameterComponent` | 范围/大小关系校验并触发 `OnThresholdsChanged` |
| `TranslationService` | `INotifyPropertyChanged` 单例，基于 `ResourceManager`，同时设置 `DefaultThreadCurrent*` 以覆盖后台线程 |
| `AppLogger` | 单例日志，INFO/WARN/ERROR 三级，上限 2000 条，自动封送到 UI 线程 |

---

## 国际化

- 默认语言为**英文**（点击标题栏 **EN/中** 切换到中文）
- 所有面向用户的 UI 文案由 `.resx` 资源文件管理
- 新增语言：复制 `Strings.en.resx`，重命名为 `Strings.xx.resx` 并翻译即可

> **说明：** 各驱动/组件输出的诊断日志目前仍为中文，仅出现在**日志**面板，尚未国际化。

---

## 环境要求

- .NET 8 SDK（仅编译需要；下方发布包已自包含运行时）
- Windows 10/11（WPF）
- NuGet 包（自动还原）：
  - `OpenCvSharp5` – OpenCV 5 绑定
  - `OpenCvSharp5.runtime.win` – OpenCV 原生库
  - `OpenCvSharp5.WpfExtensions` – `BitmapSource` 转换
  - `ZXing.Net` – 二维码/条码解码
  - `AWSSDK.S3` / `AWSSDK.Lambda` / `AWSSDK.SimpleNotificationService` – AWS 集成
  - `NModbus` / `OPCFoundation.NetStandard.Opc.Ua.*` – 工业协议
  - `Microsoft.Data.Sqlite` – 本地报工数据存储

### 配置说明

`VirtualPathVision/appsettings.json` 只在**启动时读取一次**，修改后需重启程序才会生效。
界面偏好（语言、主题、侧栏展开状态）保存在可执行文件同目录的 `user_settings.json`。

| 配置段 | 内容 |
|--------|------|
| `Industrial` | Modbus TCP、OPC UA、串口条码枪、TCP 条码枪、报工数据库 |
| `AI` | YOLO 模型路径、置信度 / NMS 阈值、输入尺寸、最大丢失帧数 |
| `AWS` | 区域、S3 桶、IoT 端点与证书路径、Topic 前缀、Lambda 函数 |

---

## 快速开始

### 直接下载（无需编译）

从 [Releases](../../releases/latest) 下载最新的自包含包：

1. 下载 `VirtualPathVision-win-x64-vX.Y.Z.zip`
2. 解压到任意目录
3. 运行 `VirtualPathVision.exe`

### 从源码构建

```bash
# 克隆仓库
git clone https://github.com/virtual-path/virtual-path-vision.git
cd virtual-path-vision

# 还原并编译
dotnet restore
dotnet build -c Release

# 运行
dotnet run --project VirtualPathVision/VirtualPathVision.csproj
```

或用 Visual Studio 2022 打开 `VirtualPathVision.sln`，按 **F5** 运行。

### 使用内置测试图片

在"加载图片"对话框中选择 `TestImages/` 目录下的图片：

| 图片 | 测试内容 |
|------|----------|
| `test_scene.png` | 全部处理模式——边缘、轮廓、形状、颜色、QR（内容为 `https://github.com/virtual-path/virtual-path-vision`）与条码（内容为 `OPENCV5`） |
| `template_green.png` | 模板匹配（绿色方块，匹配度 ≈ 1.0） |
| `template_qr.png` | 特征点匹配（纹理丰富的 QR 裁剪图） |
| `face_lena.jpg` | 人脸检测 |

---

## 技术栈

| 技术            | 说明                           |
|-----------------|--------------------------------|
| `WPF`           | Windows 桌面 UI 框架           |
| `OpenCvSharp5`  | OpenCV 5 的 .NET 封装          |
| `AWS SDK v4`    | S3 / IoT / Lambda 集成         |
| `ZXing.Net`     | 二维码与条码解码               |
| `C#`            | 主要编程语言                   |
| `XAML`          | 界面设计与布局                 |
| `.resx`         | 国际化资源文件                 |

---

## 贡献

欢迎参与贡献！请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 与[行为准则](CODE_OF_CONDUCT.md)。
安全问题请按 [SECURITY.md](SECURITY.md) 私密上报。

---

## 许可证

基于 [GNU 通用公共许可证 v3.0](LICENSE) 发布。
© 2026 xianshi3 及贡献者。
