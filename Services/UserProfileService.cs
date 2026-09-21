using MartinsWeb.Data;
using Microsoft.EntityFrameworkCore;

namespace MartinsWeb.Services;

public class UserProfileService(AppDbContext db, AuthService auth)
{
    public async Task<bool> IsUsernameTakenAsync(string username, int excludeUserId)
    {
        var others = await db.Users.Where(u => u.Id != excludeUserId)
                                   .Select(u => u.Username).ToListAsync();
        return others.Any(n => string.Equals(n?.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> IsEmailTakenAsync(string email, int excludeUserId)
    {
        var others = await db.Users.Where(u => u.Id != excludeUserId)
                                   .Select(u => u.Email).ToListAsync();
        return others.Any(e => string.Equals(e?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // Assumes AuthService.LoginAsync(email, password) returns null on failure (as in Program.cs).
    public async Task<bool> PasswordMatchesAsync(int userId, string password)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);
        return u != null && await auth.LoginAsync(u.Email, password) != null;
    }
}
