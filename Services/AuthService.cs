using MartinsWeb.Data;
using MartinsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace MartinsWeb.Services
{
    public class AuthService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        public async Task<User?> RegisterAsync(string username, string email, string password)
        {
            if (_db.Users.Any(u => u.Email == email))
                return null;

            var user = new User
            {
                Username     = username,
                Email        = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                IsAdmin      = false
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            return user;
        }

        public async Task<User?> LoginAsync(string email, string password)
        {
            var user = _db.Users.FirstOrDefault(u => u.Email == email);
            if (user == null) return null;
            return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash) ? user : null;
        }

        public async Task<List<User>> GetAllUsersAsync()
            => await _db.Users.OrderBy(u => u.Username).ToListAsync();

        public async Task DeleteUserAsync(int userId)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user != null)
            {
                _db.Users.Remove(user);
                await _db.SaveChangesAsync();
            }
        }

        public async Task SetAdminAsync(int userId, bool isAdmin)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user != null)
            {
                user.IsAdmin = isAdmin;
                await _db.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Updates a user's Username and Email.
        /// If <paramref name="newPassword"/> is non-empty, it is hashed and stored too.
        /// </summary>
        public async Task UpdateUserAsync(int userId, string username, string email, string? newPassword)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user == null) return;

            user.Username = username;
            user.Email    = email;

            if (!string.IsNullOrWhiteSpace(newPassword))
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

            await _db.SaveChangesAsync();
        }
    }
}
