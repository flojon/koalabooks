using System.Globalization;
using System.Resources;

namespace KoalaBooks.Domain.Enums
{
    public static class AccountClassLocalization
    {
        private static readonly ResourceManager ResourceManager = new ResourceManager("KoalaBooks.Domain.AccountClass", typeof(AccountClassLocalization).Assembly);

        public static string ToLocalizedString(this AccountClass accountClass, string? culture = null)
        {
            var cultureInfo = culture != null ? new CultureInfo(culture) : CultureInfo.CurrentUICulture;
            var key = $"AccountClass_{accountClass}";
            var value = ResourceManager.GetString(key, cultureInfo);
            return value ?? accountClass.ToString();
        }
    }
}
