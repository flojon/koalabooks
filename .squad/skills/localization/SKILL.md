# SKILL: Enum Localization with .resx

## Summary
Pattern for localizing enum display names using .resx resource files and ResourceManager in .NET.

## When to Use
- You need to display enum values in multiple languages.
- Domain accuracy and translation flexibility are required.

## How It Works
1. Create a .resx file (e.g., `AccountClass.resx`) with keys like `AccountClass_Asset`, values as display names.
2. Add language-specific .resx files (e.g., `AccountClass.sv.resx`).
3. Add an extension method (e.g., `ToLocalizedString`) that uses ResourceManager to fetch the localized string by key, with fallback to enum name.
4. Use `CultureInfo.CurrentUICulture` or pass a culture override.

## Example
```csharp
public static string ToLocalizedString(this AccountClass accountClass, string? culture = null)
{
    var cultureInfo = culture != null ? new CultureInfo(culture) : CultureInfo.CurrentUICulture;
    var key = $"AccountClass_{accountClass}";
    var value = ResourceManager.GetString(key, cultureInfo);
    return value ?? accountClass.ToString();
}
```

## Benefits
- Centralizes translations, easy to update.
- Supports fallback and future languages.
- Works in backend and UI.

## Context
Used for AccountClass enum in KoalaBooks.Domain, 2026-04-18.

## UI Pattern (2026-07-25)
- **Pattern:** Always use the enum extension method (e.g., `ToLocalizedString()`) for displaying enum values in the UI, never `.ToString()`. This ensures correct language is shown based on the current UI culture.
- **Example:**
  - Table: `<td>@account.AccountClass.ToLocalizedString()</td>`
  - Dropdown: `<option value="@c">@c.ToLocalizedString()</option>`
- **Context:** Used for AccountClass in Accounts.razor. Supports Swedish and future languages. Extension method should handle fallback to .ToString() if no translation is found.
