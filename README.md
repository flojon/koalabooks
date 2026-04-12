# KoalaBooks 🐨

A bookkeeping application built with C# / .NET 10, Blazor Web App (Interactive Server), and SQLite.

## Features

- **Double-entry bookkeeping** — every journal entry must balance (debit = credit)
- **Swedish BAS-kontoplan** — import chart of accounts from CSV
- **Journal entries** — create, edit, and list transactions
- **Fiscal year management** — create and close fiscal years
- **Trial balance** — view account totals per fiscal year
- **Dashboard** — summary statistics at a glance

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)

### Run the application

```bash
cd src/KoalaBooks.Web
dotnet run
```

The app will start at `https://localhost:5001` (or the port shown in the console).

The SQLite database (`koalabooks.db`) is created automatically on first run.

### Import a chart of accounts

1. Navigate to **Accounts** in the sidebar
2. Click **Import CSV**
3. Select the `sample-bas-kontoplan.csv` file (included in the repo root)

### Run tests

```bash
dotnet test
```

## Project Structure

```
KoalaBooks.sln
├── src/
│   ├── KoalaBooks.Domain/          # Entities and enums
│   ├── KoalaBooks.Infrastructure/  # EF Core DbContext, migrations, CSV import
│   ├── KoalaBooks.Application/     # Service layer (business logic)
│   └── KoalaBooks.Web/             # Blazor Web App (UI)
├── tests/
│   └── KoalaBooks.Tests/           # xUnit tests
└── sample-bas-kontoplan.csv        # Sample Swedish BAS chart of accounts
```

## CSV Import Format

The CSV file should have two columns: `AccountNumber` and `Name`.

```csv
AccountNumber,Name
1910,Kassa
3010,Försäljning
```

Account classes are automatically derived from the first digit (per BAS):
- 1 → Asset, 2 → Liability, 3 → Revenue, 4–7 → Expense, 8 → Equity
