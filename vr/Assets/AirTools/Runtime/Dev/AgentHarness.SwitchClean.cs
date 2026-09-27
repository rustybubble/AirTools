#if UNITY_EDITOR || DEVELOPMENT_BUILD
namespace AirTools.Dev
{
    // switchclean: a world-model switch closes every open UI, and measurements are fully world-model specific. In Play
    // (the backend's kitchen and Zabel gym: ServerConfig.SetOverride("http://127.0.0.1:8004") before Play):
    //   unity command eval --code 'return AirTools.Dev.AgentHarness.SwitchCloseCheck();'   then poll SwitchCloseResult()
    //   unity command eval --code 'return AirTools.Dev.AgentHarness.MeasureScopeCheck();'  then poll MeasureScopeResult()
    // (true: the same through Model view's wheel path, the gym on the table). Results are [AirTools.Check] switchclose.* /
    // measurescope.* lines. MeasureScopeCheck ends on the gym with the kitchen's items parked: the capture state.
    public static partial class AgentHarness
    {
        /// Everything that can be up at once, a real switch (all closed, logged); every main-slot card through the close
        /// run; the checkout kept through a switch while Pay is held, closed right after (nothing pays).
        public static string SwitchCloseCheck(bool modelView = false) => AirTools.Dev.SwitchCloseCheck.Run(modelView);

        public static string SwitchCloseResult() => AirTools.Dev.SwitchCloseCheck.Result();

        /// Every measurement kind drawn on the kitchen, the gym (World, or Model view), undo there, the kitchen again, the
        /// gym again: zero foreign renderers of each kind.
        public static string MeasureScopeCheck(bool modelView = false) => AirTools.Dev.MeasureScopeCheck.Run(modelView);

        public static string MeasureScopeResult() => AirTools.Dev.MeasureScopeCheck.Result();

        /// The last switch's close run: "Switched to <site>: closed [...]" (and what was kept).
        public static string LastSwitchClose() => $"{AirTools.SwitchClose.LastLine} (runs {AirTools.SwitchClose.Runs})";

        /// Close everything as a switch would, without switching (a probe; the log line names the site given).
        public static string CloseAllForSwitch(string site = "probe") => AppCommands.CloseAllForSwitch(site);
    }
}
#endif
