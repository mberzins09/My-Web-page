using Microsoft.Data.Sqlite;
using MartinsWeb.Models;

namespace MartinsWeb.Services
{
    /// <summary>
    /// "Aptaujas" - the admin sets one question (e.g. "name 5 favorite pets"); anyone with the link
    /// answers in their own words, no login needed. Only the admin can see the answers. The vote page
    /// is never linked from the site, so only people the admin sends the link to (/aptauja/{id}) can
    /// reach it. Uses app.db directly, like SuggestionService - see that class for why.
    /// </summary>
    public class SurveyService
    {
        private readonly string _cs;

        public SurveyService(IConfiguration config)
        {
            _cs = config.GetConnectionString("AppConnection")
                  ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "app.db")}";
        }

        private static async Task EnsureSchemaAsync(SqliteConnection con)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Surveys (
                    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    Question  TEXT    NOT NULL,
                    IsActive  INTEGER NOT NULL DEFAULT 1,
                    CreatedAt TEXT    NOT NULL
                );
                CREATE TABLE IF NOT EXISTS SurveyResponses (
                    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    SurveyId  INTEGER NOT NULL,
                    Text      TEXT    NOT NULL,
                    CreatedAt TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_SurveyResponses_SurveyId ON SurveyResponses (SurveyId);";
            await cmd.ExecuteNonQueryAsync();
        }

        // ── Admin: manage surveys ───────────────────────────────────────────

        public async Task<List<SurveyAdminVm>> GetAllAsync()
        {
            var result = new List<SurveyAdminVm>();

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT s.Id, s.Question, s.IsActive, s.CreatedAt,
                       (SELECT COUNT(*) FROM SurveyResponses r WHERE r.SurveyId = s.Id)
                FROM   Surveys s
                ORDER BY s.Id DESC";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                result.Add(new SurveyAdminVm
                {
                    Id            = r.GetInt32(0),
                    Question      = r.GetString(1),
                    IsActive      = r.GetInt32(2) == 1,
                    CreatedAt     = DateTime.TryParse(r.GetString(3), out var d) ? d : DateTime.MinValue,
                    ResponseCount = r.GetInt32(4),
                });
            }

            return result;
        }

        /// <summary>Creates a survey with just a question. Returns the new id.</summary>
        public async Task<int> CreateAsync(string question)
        {
            question = (question ?? "").Trim();
            if (question.Length == 0)
                throw new ArgumentException("Question can't be empty.");

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var ins = con.CreateCommand();
            ins.CommandText = @"
                INSERT INTO Surveys (Question, IsActive, CreatedAt) VALUES ($q, 1, $d);
                SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$q", question);
            ins.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("o"));
            return Convert.ToInt32(await ins.ExecuteScalarAsync());
        }

        public async Task UpdateQuestionAsync(int surveyId, string question)
        {
            question = (question ?? "").Trim();
            if (question.Length == 0)
                throw new ArgumentException("Question can't be empty.");

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE Surveys SET Question = $q WHERE Id = $id";
            cmd.Parameters.AddWithValue("$q",  question);
            cmd.Parameters.AddWithValue("$id", surveyId);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task SetActiveAsync(int surveyId, bool isActive)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE Surveys SET IsActive = $a WHERE Id = $id";
            cmd.Parameters.AddWithValue("$a",  isActive ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", surveyId);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task DeleteAsync(int surveyId)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);
            await using var tr = await con.BeginTransactionAsync();

            async Task Exec(string sql)
            {
                var c = con.CreateCommand();
                c.Transaction = (SqliteTransaction)tr;
                c.CommandText = sql;
                c.Parameters.AddWithValue("$sid", surveyId);
                await c.ExecuteNonQueryAsync();
            }

            await Exec("DELETE FROM SurveyResponses WHERE SurveyId = $sid");
            await Exec("DELETE FROM Surveys          WHERE Id      = $sid");

            await tr.CommitAsync();
        }

        public async Task DeleteAnswerAsync(int answerId)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM SurveyResponses WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", answerId);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>The question and every answer given, newest first. Admin-only - the caller checks that.</summary>
        public async Task<SurveyResults?> GetResultsAsync(int surveyId)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var head = con.CreateCommand();
            head.CommandText = "SELECT Question, IsActive FROM Surveys WHERE Id = $id";
            head.Parameters.AddWithValue("$id", surveyId);
            string question;
            bool isActive;
            await using (var r = await head.ExecuteReaderAsync())
            {
                if (!await r.ReadAsync()) return null;
                question = r.GetString(0);
                isActive = r.GetInt32(1) == 1;
            }

            var result = new SurveyResults { Id = surveyId, Question = question, IsActive = isActive };

            var ans = con.CreateCommand();
            ans.CommandText = "SELECT Id, Text, CreatedAt FROM SurveyResponses WHERE SurveyId = $id ORDER BY Id DESC";
            ans.Parameters.AddWithValue("$id", surveyId);
            await using (var r = await ans.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    result.Answers.Add(new SurveyAnswerEntry
                    {
                        Id        = r.GetInt32(0),
                        Text      = r.GetString(1),
                        CreatedAt = DateTime.TryParse(r.GetString(2), out var d) ? d : DateTime.MinValue,
                    });
                }
            }

            return result;
        }

        // ── Public: answer ───────────────────────────────────────────────────

        /// <summary>The survey's question, for the answer page. Null when the id doesn't exist.</summary>
        public async Task<SurveyEntry?> GetForAnsweringAsync(int surveyId)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT Question, IsActive FROM Surveys WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", surveyId);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;

            return new SurveyEntry { Id = surveyId, Question = r.GetString(0), IsActive = r.GetInt32(1) == 1 };
        }

        /// <summary>Records one free-text answer. Returns false when the survey is closed or doesn't exist.</summary>
        public async Task<bool> AnswerAsync(int surveyId, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;
            if (text.Length > 4000) text = text[..4000];   // guard against pasted essays / abuse

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var check = con.CreateCommand();
            check.CommandText = "SELECT IsActive FROM Surveys WHERE Id = $id";
            check.Parameters.AddWithValue("$id", surveyId);
            var active = await check.ExecuteScalarAsync();
            if (active == null || Convert.ToInt32(active) != 1) return false;

            var ins = con.CreateCommand();
            ins.CommandText = "INSERT INTO SurveyResponses (SurveyId, Text, CreatedAt) VALUES ($sid, $t, $d)";
            ins.Parameters.AddWithValue("$sid", surveyId);
            ins.Parameters.AddWithValue("$t",   text);
            ins.Parameters.AddWithValue("$d",   DateTime.UtcNow.ToString("o"));
            await ins.ExecuteNonQueryAsync();

            return true;
        }
    }
}
