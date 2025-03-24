using Labb2.Models;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Xml.Linq;
using System.Globalization;
using System.Linq;

namespace Labb2.Namespace
{
    public class TransactionsController : Controller
    {
        private static readonly HttpClient client = new HttpClient();
        public static readonly SqliteConnection sqlite;
        private const string ConnectionString = "Data Source=transactions.db";

        static TransactionsController()
        {
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer 24e9164142a8a25b4416fb6a834302c9f9d6164c");
            EnsureDatabaseCreated();
            sqlite = new SqliteConnection(ConnectionString);
        }

        public ActionResult Index()
        {
            return View();
        }

        public ActionResult FetchAPI()
        {
            List<Transaction> listOfTransactions = new List<Transaction>();
            string jsonResult = string.Empty;

            try
            {
                using (HttpResponseMessage response = client.GetAsync("https://bank.stuxberg.se/api/iban/SE4550000000058398257466/").Result)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        jsonResult = response.Content.ReadAsStringAsync().Result;

                        // Se till att vi deserialiserar direkt till en lista
                        listOfTransactions = JsonSerializer.Deserialize<List<Transaction>>(jsonResult, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }) ?? new List<Transaction>();

                        // Spara transaktionerna i databasen
                        SaveTransactionsToDatabase(listOfTransactions);
                    }
                    else
                    {
                        Console.WriteLine("Fel vid hämtning av API-data: " + response.StatusCode);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Fel vid API-anrop: " + ex.Message);
            }

            // Visa datan i vyn
            return View("DisplayTransactions", listOfTransactions);
        }

        private void SaveTransactionsToDatabase(List<Transaction> transactions)
        {
            Console.WriteLine($"\n=== Starting SaveTransactionsToDatabase for {transactions.Count} transactions ===");
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var trans in transactions)
                    {
                        Console.WriteLine($"\nProcessing transaction ID: {trans.TransactionID}");
                        Console.WriteLine($"Reference: {trans.Reference}");
                        Console.WriteLine($"Current category: {trans.Category}");

                        // First check if transaction already exists and has a category
                        string checkExistingQuery = @"
                            SELECT Category FROM Transactions 
                            WHERE TransactionID = @TransactionID";

                        string categoryToUse = "Övrigt";
                        using (var command = new SqliteCommand(checkExistingQuery, connection, transaction))
                        {
                            command.Parameters.AddWithValue("@TransactionID", trans.TransactionID);
                            var existingCategory = command.ExecuteScalar();
                            if (existingCategory != null)
                            {
                                categoryToUse = existingCategory.ToString();
                                Console.WriteLine($"Found existing category: {categoryToUse}");
                            }
                            else
                            {
                                Console.WriteLine("No existing category found, checking rules...");
                                // If no existing category, check rules
                                categoryToUse = GetCategoryForReference(trans.Reference);
                                Console.WriteLine($"Category from rules: {categoryToUse}");
                            }
                        }

                        // Save the transaction with the determined category
                        string insertQuery = @"
                        INSERT INTO Transactions (TransactionID, BookingDate, TransactionDate, Reference, Amount, Balance, Category) 
                        VALUES (@TransactionID, @BookingDate, @TransactionDate, @Reference, @Amount, @Balance, @Category)
                        ON CONFLICT(TransactionID) DO UPDATE 
                        SET BookingDate = excluded.BookingDate,
                            TransactionDate = excluded.TransactionDate,
                            Reference = excluded.Reference,
                            Amount = excluded.Amount,
                            Balance = excluded.Balance,
                            Category = CASE 
                                WHEN Transactions.Category IS NULL OR Transactions.Category = 'Övrigt' 
                                THEN excluded.Category 
                                ELSE Transactions.Category 
                            END;";

                        using (var command = new SqliteCommand(insertQuery, connection, transaction))
                        {
                            command.Parameters.AddWithValue("@TransactionID", trans.TransactionID);
                            command.Parameters.AddWithValue("@BookingDate", trans.BookingDate.ToString("yyyy-MM-dd"));
                            command.Parameters.AddWithValue("@TransactionDate", trans.TransactionDate.ToString("yyyy-MM-dd"));
                            command.Parameters.AddWithValue("@Reference", trans.Reference);
                            command.Parameters.AddWithValue("@Amount", trans.Amount);
                            command.Parameters.AddWithValue("@Balance", trans.Balance);
                            command.Parameters.AddWithValue("@Category", categoryToUse);

                            command.ExecuteNonQuery();
                            Console.WriteLine($"Saved transaction with category: {categoryToUse}");
                        }

                        // Ensure the category exists in Categories table
                        string checkCategoryQuery = @"
                            SELECT COUNT(*) FROM Categories 
                            WHERE CategoryName = @CategoryName";

                        using (var command = new SqliteCommand(checkCategoryQuery, connection, transaction))
                        {
                            command.Parameters.AddWithValue("@CategoryName", categoryToUse);
                            var count = Convert.ToInt32(command.ExecuteScalar());

                            if (count == 0)
                            {
                                Console.WriteLine($"Creating new category: {categoryToUse}");
                                string insertCategoryQuery = @"
                                    INSERT INTO Categories (TransactionID, Reference, CategoryName) 
                                    VALUES (@TransactionID, @Reference, @CategoryName)";

                                using (var command2 = new SqliteCommand(insertCategoryQuery, connection, transaction))
                                {
                                    command2.Parameters.AddWithValue("@TransactionID", trans.TransactionID);
                                    command2.Parameters.AddWithValue("@Reference", trans.Reference);
                                    command2.Parameters.AddWithValue("@CategoryName", categoryToUse);
                                    command2.ExecuteNonQuery();
                                    Console.WriteLine($"Created new category: {categoryToUse}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"Category {categoryToUse} already exists");
                            }
                        }
                    }
                    transaction.Commit();
                    Console.WriteLine("\n=== Completed SaveTransactionsToDatabase ===");
                }
            }
        }

        public async Task<ActionResult> DisplayTransactions()
        {
            // Försök hämta transaktioner från databasen först
            List<Transaction> transactions = new List<Transaction>();
            
            // Använd SQLResult för att hämta data från databasen
            XElement transactionsXml = await GetTransactionsAsXML();
            
            // Kontrollera om vi fick några transaktioner från databasen
            if (transactionsXml.Elements().Any())
            {
                // Konvertera XML-data till Transaction-objekt
                foreach (var element in transactionsXml.Elements())
                {
                    transactions.Add(new Transaction
                    {
                        TransactionID = int.Parse(element.Element("TransactionID").Value),
                        BookingDate = DateTime.Parse(element.Element("BookingDate").Value),
                        TransactionDate = DateTime.Parse(element.Element("TransactionDate").Value),
                        Reference = element.Element("Reference").Value,
                        Amount = decimal.Parse(element.Element("Amount").Value, System.Globalization.CultureInfo.InvariantCulture),
                        Balance = decimal.Parse(element.Element("Balance").Value, System.Globalization.CultureInfo.InvariantCulture),
                        Category = element.Element("Category").Value
                    });
                }
            }
            else
            {
                // Om databasen är tom, hämta från API istället
                return RedirectToAction("FetchAPI");
            }

            return View("DisplayTransactions", transactions);
        }

        private static void EnsureDatabaseCreated()
        {
            using (var connection = new SqliteConnection("Data Source=transactions.db"))
            {
                connection.Open();

                // Skapa Transactions-tabellen
                string createTableCommand = @"
                    CREATE TABLE IF NOT EXISTS Transactions (
                        TransactionID INTEGER PRIMARY KEY,
                        BookingDate TEXT,
                        TransactionDate TEXT,
                        Reference TEXT,
                        Amount TEXT,
                        Balance TEXT,
                        Category TEXT
                    )";

                using (var command = new SqliteCommand(createTableCommand, connection))
                {
                    command.ExecuteNonQuery();
                }

                // Skapa Categories-tabellen
                string createCategoriesTableCommand = @"
                    CREATE TABLE IF NOT EXISTS Categories (
                        CategoryID INTEGER PRIMARY KEY AUTOINCREMENT,
                        TransactionID INTEGER,
                        Reference TEXT,
                        CategoryName TEXT NOT NULL,
                        FOREIGN KEY (TransactionID) REFERENCES Transactions(TransactionID)
                    )";

                using (var command = new SqliteCommand(createCategoriesTableCommand, connection))
                {
                    command.ExecuteNonQuery();
                }

                // Skapa CategoryRules-tabellen
                string createCategoryRulesTableCommand = @"
                    CREATE TABLE IF NOT EXISTS CategoryRules (
                        RuleID INTEGER PRIMARY KEY AUTOINCREMENT,
                        Reference TEXT NOT NULL UNIQUE,
                        CategoryName TEXT NOT NULL,
                        CreatedAt TEXT DEFAULT (datetime('now'))
                    )";

                using (var command = new SqliteCommand(createCategoryRulesTableCommand, connection))
                {
                    command.ExecuteNonQuery();
                    Console.WriteLine("CategoryRules table created or already exists");
                }

                // Verifiera att tabellen finns och har rätt struktur
                string checkTableQuery = "SELECT name FROM sqlite_master WHERE type='table' AND name='CategoryRules'";
                using (var command = new SqliteCommand(checkTableQuery, connection))
                {
                    var result = command.ExecuteScalar();
                    Console.WriteLine($"CategoryRules table exists: {result != null}");
                }

                // Lägg till CategoryName-kolumnen om den inte finns
                try
                {
                    string alterTableCommand = "ALTER TABLE Categories ADD COLUMN CategoryName TEXT NOT NULL DEFAULT 'Övrigt'";
                    using (var command = new SqliteCommand(alterTableCommand, connection))
                    {
                        command.ExecuteNonQuery();
                    }
                }
                catch (Exception)
                {
                    // Ignorera felet om kolumnen redan finns
                }
            }
        }

        async Task<XElement> SQLResult(string query, string root, string nodeName, object parameters = null)
        {
            var xml = new XElement(root);

            try
            {
                await sqlite.OpenAsync();

                using (var command = new SqliteCommand(query, sqlite))
                {
                    if (parameters != null)
                    {
                        foreach (var prop in parameters.GetType().GetProperties())
                        {
                            command.Parameters.AddWithValue("@" + prop.Name, prop.GetValue(parameters));
                        }
                    }

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var element = new XElement(nodeName);
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                var value = await reader.GetFieldValueAsync<object>(i) ?? "";
                                element.Add(new XElement(reader.GetName(i), value));
                            }
                            xml.Add(element);
                        }
                    }
                }
            }
            finally
            {
                await sqlite.CloseAsync();
            }

            return xml;
        }

        private async Task<XElement> GetTransactionsAsXML()
        {
            string query = "SELECT * FROM Transactions";
            return await SQLResult(query, "Transactions", "Transaction");
        }

        // Hjälpmetod för att hämta alla unika kategorier
        private async Task<List<string>> GetAllCategories()
        {
            string query = "SELECT DISTINCT CategoryName FROM Categories ORDER BY CategoryName";
            var xml = await SQLResult(query, "Categories", "Category");
            
            var categories = new List<string>();
            foreach (var element in xml.Elements())
            {
                categories.Add(element.Element("CategoryName").Value);
            }
            return categories;
        }

        // GET: Transactions/Edit
        public async Task<IActionResult> Edit()
        {
            // Hämta alla transaktioner för att visa i listan
            string query = "SELECT * FROM Transactions ORDER BY BookingDate DESC";
            var xml = await SQLResult(query, "Transactions", "Transaction");
            
            var transactions = new List<Transaction>();
            foreach (var element in xml.Elements())
            {
                var transaction = new Transaction
                {
                    TransactionID = int.Parse(element.Element("TransactionID").Value),
                    Reference = element.Element("Reference").Value,
                    Amount = decimal.Parse(element.Element("Amount").Value, CultureInfo.InvariantCulture),
                    Balance = decimal.Parse(element.Element("Balance").Value, CultureInfo.InvariantCulture),
                    Category = element.Element("Category")?.Value
                };
                transactions.Add(transaction);
            }

            ViewBag.Transactions = transactions;
            ViewBag.Categories = await GetAllCategories();
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Edit(List<int> selectedTransactions, string category, string newCategory, bool applyToAll)
        {
            Console.WriteLine("\n=== Starting Edit Action ===");
            Console.WriteLine($"Selected transactions: {selectedTransactions?.Count ?? 0}");
            Console.WriteLine($"Category: {category}");
            Console.WriteLine($"New category: {newCategory}");
            Console.WriteLine($"Apply to all: {applyToAll}");

            if (selectedTransactions == null || selectedTransactions.Count == 0)
            {
                Console.WriteLine("No transactions selected");
                TempData["ErrorMessage"] = "Välj minst en transaktion att kategorisera.";
                return RedirectToAction(nameof(Edit));
            }

            if (string.IsNullOrEmpty(category) && string.IsNullOrEmpty(newCategory))
            {
                Console.WriteLine("No category selected");
                TempData["ErrorMessage"] = "Välj en befintlig kategori eller skriv in en ny.";
                return RedirectToAction(nameof(Edit));
            }

            string categoryToUse = !string.IsNullOrEmpty(newCategory) ? newCategory : category;
            Console.WriteLine($"Category to use: {categoryToUse}");

            try
            {
                using (var connection = new SqliteConnection(ConnectionString))
                {
                    await connection.OpenAsync();

                    using (var dbTransaction = connection.BeginTransaction())
                    {
                        try
                        {
                            // Get the reference from the first selected transaction
                            string getReferenceQuery = "SELECT Reference FROM Transactions WHERE TransactionID = @TransactionID";
                            string reference = null;
                            using (var command = new SqliteCommand(getReferenceQuery, connection, dbTransaction))
                            {
                                command.Parameters.AddWithValue("@TransactionID", selectedTransactions[0]);
                                reference = (string)await command.ExecuteScalarAsync();
                            }

                            Console.WriteLine($"Reference from first transaction: {reference}");

                            if (string.IsNullOrEmpty(reference))
                            {
                                throw new Exception("Kunde inte hitta referens för den valda transaktionen.");
                            }

                            if (applyToAll)
                            {
                                Console.WriteLine("Creating/updating rule for all transactions with this reference");
                                // Create or update the rule
                                string upsertRuleQuery = @"
                                    INSERT OR REPLACE INTO CategoryRules (Reference, CategoryName) 
                                    VALUES (@Reference, @CategoryName)";
                                
                                using (var command = new SqliteCommand(upsertRuleQuery, connection, dbTransaction))
                                {
                                    command.Parameters.AddWithValue("@Reference", reference);
                                    command.Parameters.AddWithValue("@CategoryName", categoryToUse);
                                    await command.ExecuteNonQueryAsync();
                                    Console.WriteLine($"Created/updated rule: {reference} -> {categoryToUse}");
                                }

                                // Update ALL transactions with this reference
                                string updateAllQuery = "UPDATE Transactions SET Category = @Category WHERE Reference = @Reference";
                                using (var command = new SqliteCommand(updateAllQuery, connection, dbTransaction))
                                {
                                    command.Parameters.AddWithValue("@Category", categoryToUse);
                                    command.Parameters.AddWithValue("@Reference", reference);
                                    var rowsAffected = await command.ExecuteNonQueryAsync();
                                    Console.WriteLine($"Updated {rowsAffected} transactions with reference {reference}");
                                }
                            }
                            else
                            {
                                Console.WriteLine("Updating only selected transactions");
                                // Update only the selected transactions
                                foreach (var transactionId in selectedTransactions)
                                {
                                    string updateSelectedTransactionsQuery = "UPDATE Transactions SET Category = @Category WHERE TransactionID = @TransactionID";
                                    using (var command = new SqliteCommand(updateSelectedTransactionsQuery, connection, dbTransaction))
                                    {
                                        command.Parameters.AddWithValue("@Category", categoryToUse);
                                        command.Parameters.AddWithValue("@TransactionID", transactionId);
                                        await command.ExecuteNonQueryAsync();
                                        Console.WriteLine($"Updated transaction {transactionId} to category {categoryToUse}");
                                    }
                                }
                            }

                            // Ensure the category exists in Categories table
                            string checkCategoryQuery = "SELECT COUNT(*) FROM Categories WHERE CategoryName = @CategoryName";
                            using (var command = new SqliteCommand(checkCategoryQuery, connection, dbTransaction))
                            {
                                command.Parameters.AddWithValue("@CategoryName", categoryToUse);
                                var count = Convert.ToInt32(await command.ExecuteScalarAsync());

                                if (count == 0)
                                {
                                    Console.WriteLine($"Creating new category: {categoryToUse}");
                                    string insertCategoryQuery = "INSERT INTO Categories (CategoryName) VALUES (@CategoryName)";
                                    using (var command2 = new SqliteCommand(insertCategoryQuery, connection, dbTransaction))
                                    {
                                        command2.Parameters.AddWithValue("@CategoryName", categoryToUse);
                                        await command2.ExecuteNonQueryAsync();
                                        Console.WriteLine($"Created new category: {categoryToUse}");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"Category {categoryToUse} already exists");
                                }
                            }

                            dbTransaction.Commit();
                            Console.WriteLine("Transaction committed successfully");
                            TempData["SuccessMessage"] = "Kategorierna har uppdaterats.";
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error during transaction: {ex.Message}");
                            dbTransaction.Rollback();
                            TempData["ErrorMessage"] = $"Ett fel uppstod: {ex.Message}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in Edit action: {ex.Message}");
                TempData["ErrorMessage"] = $"Ett fel uppstod: {ex.Message}";
            }

            Console.WriteLine("=== Completed Edit Action ===\n");
            return RedirectToAction(nameof(DisplayTransactions));
        }

        private async Task<List<Transaction>> GetAllTransactions()
        {
            string query = "SELECT * FROM Transactions ORDER BY BookingDate DESC";
            var xml = await SQLResult(query, "Transactions", "Transaction");
            
            var transactions = new List<Transaction>();
            foreach (var element in xml.Elements())
            {
                var transaction = new Transaction
                {
                    TransactionID = int.Parse(element.Element("TransactionID").Value),
                    Reference = element.Element("Reference").Value,
                    Amount = decimal.Parse(element.Element("Amount").Value, CultureInfo.InvariantCulture),
                    Balance = decimal.Parse(element.Element("Balance").Value, CultureInfo.InvariantCulture),
                    Category = element.Element("Category")?.Value
                };
                transactions.Add(transaction);
            }
            return transactions;
        }

        private List<Rule> GetRules()
        {
            Console.WriteLine("\n=== Fetching Rules ===");
            List<Rule> rules = new List<Rule>();
            try
            {
                using (var connection = new SqliteConnection(ConnectionString))
                {
                    connection.Open();

                    string query = "SELECT RuleID, Reference, CategoryName, CreatedAt FROM CategoryRules";
                    using (var command = new SqliteCommand(query, connection))
                    {
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var rule = new Rule
                                {
                                    RuleID = Convert.ToInt32(reader["RuleID"]),
                                    Reference = reader["Reference"].ToString(),
                                    CategoryName = reader["CategoryName"].ToString(),
                                    CreatedAt = DateTime.Parse(reader["CreatedAt"].ToString())
                                };
                                rules.Add(rule);
                                Console.WriteLine($"Found rule: {rule.Reference} -> {rule.CategoryName}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching rules: {ex.Message}");
                TempData["ErrorMessage"] = $"Error fetching rules: {ex.Message}";
            }

            Console.WriteLine($"Total rules found: {rules.Count}");
            Console.WriteLine("=== Completed Fetching Rules ===\n");
            return rules;
        }

        private string GetCategoryForReference(string reference)
        {
            Console.WriteLine($"\n=== Getting Category for Reference: {reference} ===");
            var rules = GetRules();
            var matchingRule = rules.FirstOrDefault(r => r.Reference == reference);
            
            if (matchingRule != null)
            {
                Console.WriteLine($"Found matching rule: {matchingRule.Reference} -> {matchingRule.CategoryName}");
            }
            else
            {
                Console.WriteLine("No matching rule found, using default category: Övrigt");
            }
            
            Console.WriteLine("=== Completed Getting Category ===\n");
            return matchingRule?.CategoryName ?? "Övrigt";
        }

        public IActionResult Summary()
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                var accountSummary = new AccountSummary();

                // First get total income and expenses
                string totalQuery = @"
                    SELECT 
                        SUM(CASE WHEN CAST(Amount as DECIMAL) > 0 THEN Amount ELSE 0 END) as TotalIncome,
                        SUM(CASE WHEN CAST(Amount as DECIMAL) < 0 THEN Amount ELSE 0 END) as TotalExpenses
                    FROM Transactions";

                using (var command = new SqliteCommand(totalQuery, connection))
                {
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            accountSummary.TotalIncome = Convert.ToDecimal(reader["TotalIncome"]);
                            accountSummary.TotalExpenses = Convert.ToDecimal(reader["TotalExpenses"]);
                        }
                    }
                }

                // Then get category breakdown
                string categoryQuery = @"
                    SELECT 
                        Category,
                        COUNT(*) as TransactionCount,
                        SUM(CAST(Amount as DECIMAL)) as TotalAmount
                    FROM Transactions
                    GROUP BY Category
                    ORDER BY ABS(SUM(CAST(Amount as DECIMAL))) DESC";

                using (var command = new SqliteCommand(categoryQuery, connection))
                {
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var summary = new TransactionSummary
                            {
                                Category = reader["Category"].ToString() ?? "Okategoriserad",
                                TransactionCount = Convert.ToInt32(reader["TransactionCount"]),
                                TotalAmount = Convert.ToDecimal(reader["TotalAmount"])
                            };

                            // Calculate percentage of total income or expenses
                            if (summary.IsIncome)
                            {
                                summary.Percentage = accountSummary.TotalIncome != 0 
                                    ? Math.Abs(summary.TotalAmount / accountSummary.TotalIncome * 100)
                                    : 0;
                            }
                            else
                            {
                                summary.Percentage = accountSummary.TotalExpenses != 0 
                                    ? Math.Abs(summary.TotalAmount / accountSummary.TotalExpenses * 100)
                                    : 0;
                            }

                            accountSummary.Categories.Add(summary);
                        }
                    }
                }

                return View(accountSummary);
            }
        }
    }
}
