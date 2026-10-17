namespace GameServer.Data;

public class DashboardUser
{
    public int       Id           { get; set; }
    public string    Username     { get; set; } = string.Empty;
    public string    PasswordHash { get; set; } = string.Empty;
    public string    Role         { get; set; } = "admin";
    public DateTime  CreatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt  { get; set; }
}

public class HelloWorld()
{
    public void SayHelloWorld(string name)
    {
        var concatName = $"name:{name}";
        Console.WriteLine($"Hello world, {concatName}");
    }
}