namespace MartinsWeb.Services
{
    public static class PointsCalculator
    {
        /// <summary>
        /// Dispatches to the correct calculator based on calculationType.
        /// Defaults to Football when not specified (backwards compatible).
        /// </summary>
        public static int Calculate(
            int predHome, int predAway, bool predOT,
            int actualHome, int actualAway, bool actualOT,
            string stage, string calculationType = "Football",
            int? actualHomeFullTime = null, int? actualAwayFullTime = null,
            int? predHomeFullTime = null, int? predAwayFullTime = null)
        {
            bool isPlayoff = !stage.Contains("group", StringComparison.OrdinalIgnoreCase)
                  && !stage.Contains("grupa", StringComparison.OrdinalIgnoreCase);

            // For Football/Football3 playoffs: compare predicted FT vs actual FT
            if (isPlayoff && (calculationType == "Football" || calculationType == "Football3") && actualHomeFullTime != null && actualAwayFullTime != null)
            {
                int ph = predHomeFullTime ?? predHome;   // fallback to main pred if FT not filled
                int pa = predAwayFullTime ?? predAway;
                return calculationType == "Football3"
                    ? CalculateFootball3(ph, pa, actualHomeFullTime.Value, actualAwayFullTime.Value, stage)
                    : CalculateFootball(ph, pa, actualHomeFullTime.Value, actualAwayFullTime.Value, stage);
            }

            return calculationType switch
            {
                var t when t.Equals("Hockey", StringComparison.OrdinalIgnoreCase)
                    => CalculateHockey(predHome, predAway, predOT, actualHome, actualAway, actualOT, stage),
                var t when t.Equals("Hockey2", StringComparison.OrdinalIgnoreCase)
                    => CalculateHockey2(predHome, predAway, predOT, actualHome, actualAway, actualOT, stage),
                var t when t.Equals("Football2", StringComparison.OrdinalIgnoreCase)
                    => CalculateFootball2(predHome, predAway, predOT, actualHome, actualAway, actualOT, stage),
                var t when t.Equals("Football3", StringComparison.OrdinalIgnoreCase)
                    => CalculateFootball3(predHome, predAway, actualHome, actualAway, stage),
                _ => CalculateFootball(predHome, predAway, actualHome, actualAway, stage)
            };
        }

        public static int CalculateFootball(int predHome, int predAway, int actualHome, int actualAway, string stage)
        {
            int points = 0;

            if (predHome == actualHome && predAway == actualAway)
                points = 3;
            else if ((predHome - predAway) == (actualHome - actualAway))
                points = 2;
            else if (Math.Sign(predHome - predAway) == Math.Sign(actualHome - actualAway))
                points = 1;
            else
                points = 0;

            return points;
        }

        public static int CalculateFootball3(int predHome, int predAway, int actualHome, int actualAway, string stage)
        {
            int points = 0;

            if (predHome == actualHome && predAway == actualAway)
                points = 5;
            else if ((predHome - predAway) == (actualHome - actualAway))
                points = 3;
            else if (Math.Sign(predHome - predAway) == Math.Sign(actualHome - actualAway))
                points = 1;
            else
                points = 0;

            return points;
        }

        /// <summary>
        /// Hockey scoring (in hockey there are no draws — someone always wins):
        ///   5 pts — exact score AND exact OT/regular time
        ///   3 pts — correct winner + correct goal difference (wrong OT, or same diff different scores)
        ///   2 pts — correct winner + goal-difference delta 1–3
        ///   1 pt  — correct winner + goal-difference delta > 3
        ///           OR wrong winner but both predicted and actual were overtime
        ///   0 pts — wrong winner (and not both-OT case above)
        /// </summary>
        public static int CalculateHockey(
            int predHome, int predAway, bool predOT,
            int actualHome, int actualAway, bool actualOT,
            string stage)
        {
            int predDiff   = predHome - predAway;
            int actualDiff = actualHome - actualAway;
            bool correctWinner = Math.Sign(predDiff) == Math.Sign(actualDiff) && predDiff != 0;

            int points;

            if (correctWinner)
            {
                bool exactScore = predHome == actualHome && predAway == actualAway;
                bool correctOT  = predOT == actualOT;
                int  diffDelta  = Math.Abs(predDiff - actualDiff);

                if (exactScore && correctOT)
                    points = 5;
                else if (diffDelta == 0)
                    points = 3;   // same margin, wrong OT or wrong exact scores
                else if (diffDelta <= 3)
                    points = 2;
                else
                    points = 1;
            }
            else
            {
                // Wrong winner: 1 pt only if both prediction and result were overtime
                points = (predOT && actualOT) ? 1 : 0;
            }

            return points;
        }

        /// <summary>
        /// Hockey2 scoring — additive bonus system, starts at 0:
        ///   +2 pts — exact home score
        ///   +1 pt  — home score off by ±1
        ///   +2 pts — exact away score
        ///   +1 pt  — away score off by ±1
        ///   +1 pt  — correct score difference (predDiff == actualDiff)
        ///            SKIPPED when OT is correctly predicted: a 1-goal OT result was
        ///            conceptually a tie going into overtime, so the diff is misleading.
        ///   +1 pt  — correct winner (no ties in hockey)
        ///   +1 pt  — correctly predicted OT (only when predOT AND actualOT are both true)
        ///   Max: 6 pts (2+2+1+1 with or without OT)
        /// </summary>
        public static int CalculateHockey2(
            int predHome, int predAway, bool predOT,
            int actualHome, int actualAway, bool actualOT,
            string stage)
        {
            int points = 0;

            // Home score
            if (predHome == actualHome)                      points += 2;
            else if (Math.Abs(predHome - actualHome) == 1)  points += 1;

            // Away score
            if (predAway == actualAway)                      points += 2;
            else if (Math.Abs(predAway - actualAway) == 1)  points += 1;

            int  predDiff   = predHome - predAway;
            int  actualDiff = actualHome - actualAway;
            bool correctOT  = predOT && actualOT; // both checked AND it was OT

            // Correct score difference — skipped when OT correctly predicted
            if (!actualOT && predDiff == actualDiff) points += 1;

            // Correct winner (no ties in hockey)
            if (Math.Sign(predDiff) == Math.Sign(actualDiff) && predDiff != 0) points += 2;

            // Correctly predicted OT (only a bonus when you checked it and it really was OT)
            if (correctOT) points += 1;

            return points;
        }

        /// <summary>
        /// Football2 scoring — additive bonus system, starts at 0:
        ///
        ///   Group stages (stage contains "group" / "grupa") — ties allowed, no OT:
        ///     +2 pts — exact home score
        ///     +1 pt  — home score off by ±1
        ///     +2 pts — exact away score
        ///     +1 pt  — away score off by ±1
        ///     +1 pt  — correct score difference (predDiff == actualDiff)
        ///     +1 pt  — correct winner; if actual is a tie: +1 for correctly predicting a tie
        ///              (both cases: Math.Sign(predDiff) == Math.Sign(actualDiff))
        ///     Max: 6 pts
        ///
        ///   Playoff stages — no ties, OT checkbox enabled; identical rules to Hockey2:
        ///     Max: 6 pts
        /// </summary>
        public static int CalculateFootball2(
            int predHome, int predAway, bool predOT,
            int actualHome, int actualAway, bool actualOT,
            string stage)
        {
            bool isGroupStage = stage.Contains("group", StringComparison.OrdinalIgnoreCase)
                             || stage.Contains("grupa", StringComparison.OrdinalIgnoreCase);

            if (isGroupStage)
            {
                int points = 0;

                // Home score
                if (predHome == actualHome)                      points += 2;
                else if (Math.Abs(predHome - actualHome) == 1)  points += 1;

                // Away score
                if (predAway == actualAway)                      points += 2;
                else if (Math.Abs(predAway - actualAway) == 1)  points += 1;

                int predDiff   = predHome - predAway;
                int actualDiff = actualHome - actualAway;

                // Correct score difference
                if (predDiff == actualDiff) points += 1;

                // Correct winner — or correct tie prediction when actual is a draw.
                // Math.Sign handles all three outcomes: home win (+1), draw (0), away win (-1).
                if (Math.Sign(predDiff) == Math.Sign(actualDiff)) points += 2;

                return points;
            }
            else
            {
                // Playoff: identical to Hockey2 (OT checkbox, no ties)
                return CalculateHockey2(predHome, predAway, predOT, actualHome, actualAway, actualOT, stage);
            }
        }
    }
}
