namespace Zentwik.Cli;

public static class Help
{
    public static void Print(string? command)
    {
        switch (command)
        {
            case "plans":
                Console.WriteLine("""
                    zentwik plans <subcommand>

                      list                        列出方案
                      show <id>                   查看方案详情
                      apply <id>                  应用方案并回读校验
                      revert <id>                 回退方案写入
                      delete <id>                 删除方案（被场景引用时拒绝）
                      save --file <path|->        从 JSON 保存方案
                      create --kind <kind> --id <id> --name <name>
                          cpuTuning  --core 核=CO值 ... [--fmax MHz]
                          power      --scheme GUID [--set 子组/设置/ac|dc=值 ...]
                          scheduling --rule 进程=掩码:优先级 ...
                    """);
                break;
            case "scenes":
                Console.WriteLine("""
                    zentwik scenes <subcommand>

                      list                        列出场景
                      show <id>                   查看场景详情
                      activate <id>               激活场景（手动优先）
                      deactivate                  停用当前场景并回退
                      enable <id> | disable <id>  启用或停用场景
                      delete <id>                 删除场景
                      save --file <path|->        从 JSON 保存场景
                      create --id <id> --name <name> --plan ID ... [--priority N] [--trigger 类型[:参数] ...]

                    触发类型
                      process:<进程名>           该进程正在运行时生效
                      processExited:<进程名>     该进程退出那一刻切换过去，之后保持（不自动回退）
                      powerSource:ac|dc          交流或电池供电时生效
                      manual                     只能手动激活

                    多个 --trigger 同时给出时须全部成立；不给则按 manual。
                    """);
                break;
            case "monitor":
                Console.WriteLine("""
                    zentwik monitor [--count N] [--interval MS]

                    订阅遥测。间隔 500–5000 ms，默认 500。
                    """);
                break;
            case "ledger":
                Console.WriteLine("""
                    zentwik ledger [release <target>]

                    列出状态账本；release 释放用户接管。
                    """);
                break;
            case "settings":
                Console.WriteLine("""
                    zentwik settings [--language zh-CN|zh-TW|en-US] [--theme dark|light]
                                     [--accent blue|violet|teal|amber] [--scale 0.8..1.6]

                    不带参数时读取当前设置。
                    """);
                break;
            case "power":
                Console.WriteLine("""
                    zentwik power <subcommand>

                      list                        列出 Windows 电源方案
                      create --template <GUID|balanced|saver|performance|ultimate> --name <name>
                                                  以模板派生新方案
                      rename --id <GUID> --name <name>
                                                  改方案显示名
                      delete --id <GUID> [--no-backup]
                                                  删除方案（默认先备份；内置拒删；活动方案先切走）
                      backup --id <GUID>          把方案的设置项与说明存成一份备份
                      backups                     列出已有备份
                      restore --backup <ID|路径>  从备份重建方案（新 GUID，名字冲突时自动加后缀）

                    备份写在服务的 power-backups 目录：一份 JSON 清单 + 一个可选的 .pow 原件。
                    恢复不会还原原 GUID，也不保证可见性——系统按当前策略决定新方案可不可见。
                    """);
                break;
            case "tuning":
                Console.WriteLine("""
                    zentwik tuning [--intel]

                    读取 CPU 调校通道状态与逐核电压偏移；--intel 读取 Intel 的电压偏移与睿频上限。不可用时给出原因。
                    """);
                break;
            case "doctor":
                Console.WriteLine("""
                    zentwik doctor

                    输出运行模式、路径、模块状态与最近错误。
                    """);
                break;
            default:
                PrintGlobal();
                break;
        }
    }

    public static void PrintGlobal()
    {
        Console.WriteLine("""
            zentwik — Zentwik 代理命令行

            用法
              zentwik <command> [options]

            命令
              status                    代理状态与能力概览
              caps                      能力清单
              modules                   内置模块状态
              monitor                   持续采样 CPU 与内存
              tuning                    读取 CPU 电压偏移
              plans                     方案管理
              scenes                    场景管理
              ledger                    状态账本
              power                     Windows 电源方案管理
              settings                  界面与语言设置
              doctor                    诊断信息
              logs                      最近日志
              help [command]            帮助
              version                   版本

            全局参数
              --json                    机器可读输出
              --pipe NAME               管道名
              -h, --help                帮助

            退出码
              0 成功   1 代理不可用   2 用法错误   3 操作失败
              4 能力不可用   5 超时   6 部分成功
            """);
    }
}
