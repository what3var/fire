using fire.Native;

namespace fire.Compiler
{
    /// <summary>The question on the command line when a C++ toolchain is needed and there is none: a text that says why, and "[F]ix automatically / [C]ancel (f):". Only when somebody is
    /// there to answer (input and output are not redirected); otherwise the build just reports that there is no compiler.</summary>
    public static class ConsoleToolchainPrompt
    {
        public static void Install(TextWriter? output = null)
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return;
            var o = output ?? Console.Out;
            ToolchainProvider.Log = o.WriteLine;
            ToolchainProvider.Ask = request =>
            {
                o.WriteLine();
                o.WriteLine(request.Message);
                o.WriteLine();
                if (!request.CanInstall) return ToolchainChoice.Cancel;
                o.Write("Fix automatically? [F]ix / [C]ancel (f): ");
                string answer = (Console.ReadLine() ?? "c").Trim().ToLowerInvariant();
                return answer is "" or "f" or "fix" or "y" or "yes" or "j" or "ja" or "b" ? ToolchainChoice.Install : ToolchainChoice.Cancel;
            };
        }
    }
}
