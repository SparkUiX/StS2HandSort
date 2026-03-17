namespace Hand_Sort.src;


using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;


// 必须要加的属性，用于注册Mod。字符串和初始化函数命名一致。
[ModInitializer("Init")]
public class ModC
{
    // 打patch（即修改游戏代码的功能）用
    private static Harmony? _harmony;

    // 初始化函数
    public static void Init()
    {
        // 传入参数随意，只要不和其他人撞车即可
        _harmony = new Harmony("sts2.u1x.handsort");
        _harmony.PatchAll();
        Log.Debug("Mod initialized!");
    }
}
