using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace MartinsWeb.Services
{
    public class UserContextService
    {
        private readonly AuthenticationStateProvider _auth;

        public UserContextService(AuthenticationStateProvider auth)
        {
            _auth = auth;
        }

        public async Task<ClaimsPrincipal> GetUserAsync()
        {
            var state = await _auth.GetAuthenticationStateAsync();
            return state.User;
        }

        public async Task<int?> GetUserIdAsync()
        {
            var user = await GetUserAsync();

            if (user.Identity?.IsAuthenticated != true)
                return null;

            return int.Parse(user.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        }

        public async Task<bool> IsAdminAsync()
        {
            var user = await GetUserAsync();
            return user.IsInRole("Admin");
        }
    }
}
