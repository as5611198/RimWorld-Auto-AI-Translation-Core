# 编译与运行依赖

恢复项目声明的 NuGet 包后，可取得 `Mono.Cecil 0.11.6` 的 `Mono.Cecil.dll`。各游戏版本的项目需引用适用的程序集，并将其随安装包提供。当前两个项目均引用包中的 `lib/net40/Mono.Cecil.dll`，设置为复制到构建输出；无需按 RimWorld 版本单独编译 Cecil。

SQLite 配套库通过仓库根目录下的 `tools/prepare-sqlite-runtime.ps1` 整理，安装包需包含对应平台的运行库及校验文件。脚本从已恢复的 `System.Data.SQLite 2.0.4` 和 `SourceGear.sqlite3 3.53.4` 包中复制文件，当前目标为 Mod 的 `1.6/Assemblies` 和 `1.6/Native/sqlite`。其他版本的适配需同时核对项目引用、运行库读取路径和包结构。

用户数据库由 Mod 首次使用时创建；已有数据库继续使用，并按需升级表结构，无需预置到安装包。

主 DLL 采用选定源码提交的构建结果，连同所需运行依赖和 Mod 资源组成发布包。
