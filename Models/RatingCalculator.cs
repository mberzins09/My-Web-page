namespace MartinsWeb.Models
{
    /// <summary>
    /// Calculates LGTF rating point difference for a single game.
    /// Ported directly from RankingApp.Core.Models.RatingCalculator.
    /// </summary>
    public static class RatingCalculator
    {
        /// <param name="myPoints">Ranking points of the player whose delta we calculate.</param>
        /// <param name="oppPoints">Ranking points of the opponent.</param>
        /// <param name="isWin">True if the player won this game.</param>
        /// <param name="coef">Tournament coefficient (e.g. 0.5, 1.0, 2.0).</param>
        /// <returns>Rating delta — positive means gained, negative means lost.</returns>
        public static int Calculate(int myPoints, int oppPoints, bool isWin, double coef)
        {
            int pD = myPoints - oppPoints;

            if (isWin)
            {
                if (pD > 0)
                {
                    if (pD >= 1   && pD <= 25)  return (int)Math.Ceiling(coef * 9);
                    if (pD >= 26  && pD <= 50)  return (int)Math.Ceiling(coef * 8);
                    if (pD >= 51  && pD <= 100) return (int)Math.Ceiling(coef * 7);
                    if (pD >= 101 && pD <= 150) return (int)Math.Ceiling(coef * 6);
                    if (pD >= 151 && pD <= 200) return (int)Math.Ceiling(coef * 5);
                    if (pD >= 201 && pD <= 300) return (int)Math.Ceiling(coef * 4);
                    if (pD >= 301 && pD <= 400) return (int)Math.Ceiling(coef * 3);
                    if (pD >= 401 && pD <= 500) return (int)Math.Ceiling(coef * 2);
                    if (pD >= 501)              return (int)Math.Ceiling(coef * 1);
                }
                else
                {
                    pD = Math.Abs(pD);
                    if (pD >= 0   && pD <= 24)  return (int)Math.Ceiling(coef * 10);
                    if (pD >= 25  && pD <= 49)  return (int)Math.Ceiling(coef * 11);
                    if (pD >= 50  && pD <= 99)  return (int)Math.Ceiling(coef * 13);
                    if (pD >= 100 && pD <= 149) return (int)Math.Ceiling(coef * 15);
                    if (pD >= 150 && pD <= 199) return (int)Math.Ceiling(coef * 18);
                    if (pD >= 200 && pD <= 299) return (int)Math.Ceiling(coef * 21);
                    if (pD >= 300 && pD <= 399) return (int)Math.Ceiling(coef * 24);
                    if (pD >= 400 && pD <= 499) return (int)Math.Ceiling(coef * 28);
                    if (pD >= 500)              return (int)Math.Ceiling(coef * 32);
                }
            }
            else
            {
                if (pD > 0)
                {
                    if (pD >= 0   && pD <= 24)  return -(int)Math.Floor(coef * 9);
                    if (pD >= 25  && pD <= 49)  return -(int)Math.Floor(coef * 11);
                    if (pD >= 50  && pD <= 99)  return -(int)Math.Floor(coef * 11);
                    if (pD >= 100 && pD <= 149) return -(int)Math.Floor(coef * 12);
                    if (pD >= 150 && pD <= 199) return -(int)Math.Floor(coef * 14);
                    if (pD >= 200 && pD <= 299) return -(int)Math.Floor(coef * 16);
                    if (pD >= 300 && pD <= 399) return -(int)Math.Floor(coef * 18);
                    if (pD >= 400 && pD <= 499) return -(int)Math.Floor(coef * 21);
                    if (pD >= 500)              return -(int)Math.Floor(coef * 25);
                }
                else
                {
                    pD = Math.Abs(pD);
                    if (pD >= 1   && pD <= 25)  return -(int)Math.Floor(coef * 8);
                    if (pD >= 26  && pD <= 50)  return -(int)Math.Floor(coef * 7);
                    if (pD >= 51  && pD <= 100) return -(int)Math.Floor(coef * 6);
                    if (pD >= 101 && pD <= 150) return -(int)Math.Floor(coef * 5);
                    if (pD >= 151 && pD <= 200) return -(int)Math.Floor(coef * 4);
                    if (pD >= 201 && pD <= 300) return -(int)Math.Floor(coef * 3);
                    if (pD >= 301 && pD <= 400) return -(int)Math.Floor(coef * 2);
                    if (pD >= 401 && pD <= 500) return -(int)Math.Floor(coef * 1);
                    if (pD >= 501)              return 0;
                }
            }

            return 0;
        }
    }
}
