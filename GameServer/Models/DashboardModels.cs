namespace GameServer.Models;

public record LoginRequest(string Username, string Password);

public record LoginResponse(string Token, int ExpiresIn);
