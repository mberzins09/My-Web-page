using Microsoft.Data.Sqlite;
using MartinsWeb.Models;

namespace MartinsWeb.Services
{
    /// <summary>
    /// Anonymous suggestions/reviews left on the "Ieteikumi" page. No login is needed to submit one;
    /// only the person's text is stored, nothing that identifies them. Uses app.db directly (the
    /// same file EF Core uses for the rest of the site) so it needs no separate database file, but
    /// talks to it with plain SQL rather than through AppDbContext, so it needs no EF migration.
    /// </summary>
    public class SuggestionService
    {
        private readonly string _cs;

        public SuggestionService(IConfiguration config)
        {
            _cs = config.GetConnectionString("AppConnection")
                  ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "app.db")}";
        }

        private static async Task EnsureSchemaAsync(SqliteConnection con)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Suggestions (
                    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    Text      TEXT    NOT NULL,
                    CreatedAt TEXT    NOT NULL
                )";
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Stores a suggestion. Empty or whitespace-only text is ignored.</summary>
        public async Task AddAsync(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 4000) text = text[..4000];   // guard against pasted essays / abuse

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var cmd = con.CreateCommand();
            cmd.CommandText = "INSERT INTO Suggestions (Text, CreatedAt) VALUES ($t, $d)";
            cmd.Parameters.AddWithValue("$t", text);
            cmd.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Newest first. Admin-only - the caller is responsible for checking that.</summary>
        public async Task<List<SuggestionEntry>> GetAllAsync()
        {
            var result = new List<SuggestionEntry>();

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT Id, Text, CreatedAt FROM Suggestions ORDER BY Id DESC";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                result.Add(new SuggestionEntry
                {
                    Id        = r.GetInt32(0),
                    Text      = r.GetString(1),
                    CreatedAt = DateTime.TryParse(r.GetString(2), out var d) ? d : DateTime.MinValue,
                });
            }

            return result;
        }

        public async Task DeleteAsync(int id)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM Suggestions WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
