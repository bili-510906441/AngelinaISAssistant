using System.Windows;

namespace AngelinaISAssistant;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 依赖框架发布：缺 .NET 10 时 apphost 会先拦截；这里再兜底检查一次实跑运行时
        if (!RuntimeRequirement.IsSatisfied())
        {
            RuntimeRequirement.ShowGuidance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }
}
