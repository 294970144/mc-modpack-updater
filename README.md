# 我的世界更新助手

Minecraft 整合包增量更新工具 —— 只更新变化的部分，不动存档和设置。

## 功能特性

- 支持 PCL2 导出的 `.zip` 和 Modrinth `.mrpack` 整合包格式
- 自动探测 PCL2 启动器并匹配目标游戏版本
- 按模组 ID 精确对比，不受 PCL2 中文前缀文件名影响
- 指纹预筛（size + CRC32），重复对比秒开
- 事务性更新：双保险（目录备份 + 操作日志），任何一步失败自动回滚
- 智能区分「整合包装的」和「玩家自己装的」模组
- 支持模组、配置文件、资源包、光影包四类更新范围
- 受保护文件机制：不碰存档、截图、地图、JEI 书签等个人数据
- 一键回滚到任意历史备份
- 可打包为单文件 EXE（Node SEA）

## 快速开始

### 直接运行（需要 Node.js 22+）

```bash
npm install
npm start
```

浏览器会自动打开 `http://127.0.0.1:8091/`。

### 打包为 EXE

```bash
npm install
npm run build
```

产物在 `dist/我的世界更新助手.exe`，双击即可运行，无需 Node.js。

### 原生文件对话框（可选）

编译 `tools/McDialog.exe` 可让“浏览文件”对话框从 ~2s 降到 ~0.3s：

```
tools\build-dialog.bat
```

## 项目结构

```
src/
  config.js          配置常量
  server.js          HTTP 服务入口
  router.js          API 路由
  core/
    zip-reader.js    零依赖 ZIP 读取器（支持 ZIP64、嵌套、子区间直读）
    modpack-reader.js 整合包读取与格式识别
    mod-metadata.js  Mod 元数据解析（Fabric/Forge/NeoForge/Quilt）
    toml-lite.js     极简 TOML 解析器
    diff-engine.js   对比引擎（指纹预筛 + modId 匹配）
    manifest.js      安装记录（区分整合包/玩家自装）
    applier.js       事务性更新执行器（备份 + 日志 + 回滚）
  services/
    pcl2-detector.js PCL2 启动器探测
    client-scanner.js 游戏版本扫描与匹配
    dialog.js        系统文件选择对话框
  utils/
    fs-helpers.js    文件系统工具函数
    logger.js        日志（控制台 + SSE 推送）
    response.js      HTTP 响应工具
public/
  index.html         前端页面
  css/main.css       像素生态风样式
  js/api.js          后端通信（JSON + SSE）
  js/ui.js           UI 工具库
  js/app.js          主流程逻辑
tools/
  McDialog.cs        原生对话框 C# 源码
  build-dialog.bat   编译脚本
```

## 技术要点

- **零外部依赖**：运行时不依赖 npm 包，ZIP 解析、TOML 解析、CRC32 全部内置
- **CSRF 防御**：只接受本机 Origin 的 API 请求
- **路径穿越防御**：整合包内 `../` 路径被拒绝
- **原子写入**：先写 `.tmp` 再 `rename`，避免断电留下损坏文件
- **事件循环让出**：每处理几个文件让出一次，保证 SSE 进度帧实时推送

## 许可证

MIT
