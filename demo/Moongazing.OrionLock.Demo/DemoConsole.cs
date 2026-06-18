namespace Moongazing.OrionLock.Demo;

/// <summary>Small console helpers so the demos read consistently.</summary>
internal static class DemoConsole
{
    public static void Banner(string title)
    {
        var line = new string('=', 64);
        Console.WriteLine();
        Console.WriteLine(line);
        Console.WriteLine("  " + title);
        Console.WriteLine(line);
    }

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("--- " + title + " ---");
    }

    public static void Step(string message) => Console.WriteLine("  " + message);

    public static void Result(string message) => Console.WriteLine("  => " + message);
}
