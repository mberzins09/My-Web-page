namespace MartinsWeb.Models
{
    public static class AgeCalculator
    {
        public static int GetAge(DateTime tournamentDate, string? birthDate)
        {
            if (string.IsNullOrWhiteSpace(birthDate))
            {
                return 0;
            }

            if (!DateTime.TryParse(birthDate, out var dob))
            {
                return 0;
            }

            int age = tournamentDate.Year - dob.Year;

            if (dob.Date > tournamentDate.AddYears(-age))
            {
                age--;
            }

            return age;
        }
    }
}
