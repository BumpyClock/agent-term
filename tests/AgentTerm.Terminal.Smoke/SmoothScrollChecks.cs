using AgentTerm.Terminal;

internal static class SmoothScrollChecks
{
    public static void Run()
    {
        var motion = new TerminalScrollAnimation();
        motion.Reset(10);
        motion.Add(-3, 10, 20, true);
        motion.Advance(.07);
        Expect(motion.Position == 8.5 && motion.IntegerOffset == 8 && motion.Fraction == .5,
            "Three-row wheel motion must cross integer viewports with bounded one-row translation.");
        motion.Advance(.07);
        Expect(!motion.IsActive && motion.Position == 7, "Animation must finish without idle ticks.");
        motion.Add(-100, 7, 20, true);
        motion.Advance(1);
        Expect(motion.Position == 0 && motion.Fraction == 0, "Top edge must not reveal missing history.");
        motion.Add(100, 0, 20, true);
        motion.Advance(1);
        Expect(motion.Position == 20 && motion.Fraction == 0, "Bottom edge must not overshoot.");
        motion.Reset(10);
        motion.Add(.2, 10, 20, true);
        motion.Add(.2, 10, 20, true);
        motion.Add(.2, 10, 20, true);
        motion.Advance(.07);
        Expect(motion.Position == 10.5, "Small trackpad deltas must accumulate into fractional motion.");
        motion.Reset(11);
        motion.Advance(1);
        Expect(!motion.IsActive && motion.Position == 11, "Interruption must discard remaining motion.");
        motion.Add(-4, 11, 20, false);
        Expect(!motion.IsActive && motion.Position == 7, "Reduced motion must move immediately.");
        Expect(!TerminalScrollAnimation.UseHistoryScrolling(true, false) &&
            TerminalScrollAnimation.UseHistoryScrolling(true, true) &&
            TerminalScrollAnimation.UseHistoryScrolling(false, false), "Mouse reporting owns wheel unless Shift overrides.");
        Console.WriteLine("Smooth scroll checks passed.");
    }
    private static void Expect(bool value, string message)
    { if (!value) throw new Exception(message); }
}
