AngelinaISAssistant 版本工具（仅开发/发布端使用，不随发布包分发）
================================================================

hdiffz.exe        HDiffPatch v5.1.3（windows64），生成文件夹级增量补丁
make_update.ps1   封装 hdiffz 的目录模式，输出 .aisapatch
LICENSE.txt       HDiffPatch 许可证（MIT，作者 housisong）
                  hdiffz.exe 与 hpatchz.exe 均为该作者作品，发布包内保留同一许可证

零、发布（release 采用自包含部署，内置 .NET 运行时）
  dotnet publish -c Release -r win-x64 --self-contained true -o ..\..\release
  （或直接 .\publish_release.ps1，默认输出到 code 的上一级 release 目录）

  发布目录会自动带上 DependencySetup_依赖库安装.bat，
  用于给没有装 VC++ 运行库的机器补装依赖。

一、为什么用原生目录补丁
  HDiffPatch 本身就支持在「文件夹之间」做 diff / patch（README_cn.md 的
  「hdiffz oldPath newPath outDiffFile」与「hpatchz oldPath diffFile outNewPath」）。
  目录模式会跨文件复用相同数据，补丁通常比逐文件 hdiff 更小；
  hpatchz 直接读旧目录、写出完整的新目录，删除/新增/改名都由补丁自身表达，
  客户端不需要 manifest，也不需要解包。

二、生成更新包
  .\make_update.ps1 -OldDir ..\..\release_old -NewDir ..\release `
                    -FromVersion v0.0.2 -ToVersion v0.0.4

  等价的原生命令：

  hdiffz.exe -D -s-64m -c-lzma2-9-16m -p-8 旧目录 新目录 补丁.aisapatch

  - -D        强制目录模式（即使参数看起来像文件）
  - -s-64m    流式读取，内存占用可控，适合几十 MB 的发布目录
  - -c-...    LZMA2 压缩
  - -p-8      8 线程

  产物：angelina_isa_v{旧}_v{新}.aisapatch（默认输出到 NewDir 上一级）
  脚本会先剔除 _update_stage / _update_pkg / config / reports / logs / *.log，
  保证补丁只描述发布内容。

三、客户端应用（release 内已实现）
  hpatchz.exe -s-64m "安装目录" "补丁.aisapatch" "_update_stage"

  1. 校验补丁与当前安装目录是否匹配（校验和不符会失败）
  2. 输出完整的新目录到 _update_stage（含新增、删除、改名）
  3. 客户端比较安装目录与 stage，生成 _update_remove.txt（旧版多出来的文件）
  4. _update.cmd 等待进程退出 → 覆盖 stage → 按清单删除 → 清理 → 启动新版本
  5. 应用自动退出，由脚本拉起新版本（即「更新完成后自动打开」）

四、命名与兼容
  文件夹级增量包：angelina_isa_v{旧}_v{新}.aisapatch（唯一支持的增量格式）
  全量包：        angelina_isa_v{新}_full.zip

  旧的单文件补丁 .hdiff 已彻底移除支持：
  - 客户端不再下载、不再识别、拖入会被直接拒绝
  - 不要再生成或上传 .hdiff 包

六、CI（GitHub Actions）
  仓库根目录 = code/，工作流在 .github/workflows/release.yml。
  - push / PR：构建（告警即失败）+ 冒烟测试 + 文件夹补丁测试 + 发布 + 布局校验 + 上传 artifact
  - 打 v* 标签：额外产出 angelina_isa_v{新}_full.zip，并自动用「上一个 tag 的 full.zip」
    生成本次增量包 angelina_isa_v{旧}_v{新}.aisapatch，应用校验一致后一并挂到 Release

七、发布目录里的 externals（与 examples 一致）
  externals\
    Serilog.dll / Vortice.*.dll / SharpGen.*.dll / WinRT.Runtime.dll
    Microsoft.Windows.SDK.NET.dll
    onnxruntime.dll / onnxruntime_providers_shared.dll
    onnxruntime_LICENSE.txt
    onnxruntime_dml\
      onnxruntime.dll / onnxruntime_providers_shared.dll / DirectML.dll
      ORT-LICENSE.txt / DirectML-LICENSE.txt / DirectML-ThirdPartyNotices.txt
      ← DirectML 推理（设置里的「使用 GPU 加速推理」）
    locales\
      cs\ de\ es\ fr\ it\ ja\ ko\ pl\ pt-BR\ ru\ tr\ zh-Hans\ zh-Hant\
        *.resources.dll   ← WPF 卫星资源，由 App 的程序集解析器按语言加载

  运行时目录 runtime\shared\... 下不再保留语言子目录；
  App 通过 externals\locales\<culture>\<name>.dll 解析（含 zh-Hant → zh 回退）。

八、便携数据目录
  程序产生的全部个性化数据都在程序目录下，随 release 包一起移动：
    config\settings.json   界面语言、主题、热键、采集方式、实时辅助勾选等
    config\logs\           运行日志（按天滚动）
    config\reports\        崩溃报告与待提示标记

  旧版本存放在 %AppData%\AngelinaISAssistant 的 settings.json 会在首次启动时自动迁移到
  config\settings.json；更新包不会覆盖或删除 config 目录。

九、实时辅助的集成战略内嵌数据（Live\Intel\data）
  generate_intel_data.py 从 tomimi.dev 的集成战略数据生成四个 JSON：
    stage_names.json  关卡名别名（中/英/日、标点、变体名归一化、levelId、主题）
    stages.json       关卡代码 / 主题 / 变体修正（n_mods、all_mods、elite_mods）/ 紧急作战敌人替换 / 敌人列表
    enemies.json      关卡引用到的敌人：三语名字、类型、各等级数值、form_mods、traits
    difficulty.json   六个主题各难度的修正效果与楼层成长系数
  这些文件随主工程编译进 DLL（EmbeddedResource），发布包不额外携带文件。
  tomimi-dev 完整仓库在开发侧单独保留（不随本仓库分发），
  后续做关卡数值 / 路线模拟等功能可继续取用。

  重新生成（在 code 目录下）：
    python version_tools\generate_intel_data.py
    python version_tools\generate_intel_data.py --tomimi <tomimi-dev-main 路径> --out <输出目录>