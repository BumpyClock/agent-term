using System.Text;

internal static class FullScreenFixture
{
    public static void Run()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.TreatControlCAsInput = true;
        int selected = 0;
        string typed = "";
        bool functionKey = false;
        Console.Write("\x1b[?1049h\x1b[?1h");
        try
        {
            while (true)
            {
                var screen = new StringBuilder("\x1b[?25l\x1b[2J\x1b[H\x1b[38;2;255;255;255;48;2;30;60;120mFULL SCREEN FIXTURE\x1b[0m");
                screen.Append("\x1b[3;1HArrow keys select; F2 toggles; type; Esc exits.");
                for (int row = 0; row < 2; row++)
                {
                    screen.Append($"\x1b[{5 + row};1H");
                    if (row == selected) screen.Append("\x1b[38;2;0;0;0;48;2;80;220;140m");
                    screen.Append(row == 0 ? " First item " : " Second item ");
                    screen.Append("\x1b[0m");
                }
                screen.Append($"\x1b[8;1HTyped={typed}\x1b[9;1HF2={(functionKey ? "on" : "off")}\x1b[11;5H\x1b[6 q\x1b[?25h");
                Console.Write(screen.ToString());
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape) break;
                if (key.Key == ConsoleKey.DownArrow) selected = 1;
                else if (key.Key == ConsoleKey.UpArrow) selected = 0;
                else if (key.Key == ConsoleKey.F2) functionKey = !functionKey;
                else if (!char.IsControl(key.KeyChar)) typed += key.KeyChar;
            }
        }
        finally { Console.Write("\x1b[0m\x1b[?1l\x1b[2 q\x1b[?25h\x1b[?1049l"); }
        Console.WriteLine("FULL_SCREEN_FIXTURE_EXITED");
    }
}
