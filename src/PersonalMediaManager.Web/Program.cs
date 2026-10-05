using PersonalMediaManager.Web;

try
{
    await using var app = FoundationHost.Build(args);
    await app.RunAsync();
    return 0;
}
catch (ArgumentException error)
{
    Console.Error.WriteLine($"Invalid foundation startup: {error.Message}");
    return 2;
}
