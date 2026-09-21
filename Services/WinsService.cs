using MartinsWeb.Data;
using MartinsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace MartinsWeb.Services;

public class WinsService(AppDbContext db)
{
    public static List<int> WinnerIds(PredictionsHistory h)
    {
        if (h.Entries.Count == 0) return new();
        var max = h.Entries.Max(e => e.Points);
        if (max <= 0) return new();
        return h.Entries.Where(e => e.Points == max && e.UserId != null)
                        .Select(e => e.UserId!.Value).Distinct().ToList();
    }

    /// Awards +1 win for every history not yet counted. Safe to call repeatedly.
    public async Task<List<string>> AwardPendingWinsAsync()
    {
        var log = new List<string>();
        var pending = await db.PredictionsHistories
            .Include(h => h.Entries).Where(h => !h.WinsAwarded).ToListAsync();

        foreach (var h in pending)
        {
            foreach (var uid in WinnerIds(h))
            {
                var u = await db.Users.FindAsync(uid);
                if (u == null) continue;
                u.TournamentsWon++;
                log.Add($"{u.Username} (+1, {h.TournamentName})");
            }
            h.WinsAwarded = true;
        }
        await db.SaveChangesAsync();
        return log;
    }

    /// Resets everyone to the count derived from history. Overwrites manual edits!
    public async Task RecalculateAllAsync()
    {
        var users = await db.Users.ToListAsync();
        foreach (var u in users) u.TournamentsWon = 0;

        var histories = await db.PredictionsHistories.Include(h => h.Entries).ToListAsync();
        foreach (var h in histories)
        {
            foreach (var uid in WinnerIds(h))
            {
                var u = users.FirstOrDefault(x => x.Id == uid);
                if (u != null) u.TournamentsWon++;
            }
            h.WinsAwarded = true;
        }
        await db.SaveChangesAsync();
    }

    public async Task SetWinsAsync(int userId, int wins)
    {
        var u = await db.Users.FindAsync(userId);   // tracked instance, so other services see the change
        if (u == null) return;
        u.TournamentsWon = Math.Max(0, wins);
        await db.SaveChangesAsync();
    }
}
