using Labb2.Models;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Xml.Linq;
using System.Globalization;

namespace Labb2.Namespace
{
    public class TransactionsController : Controller
    {
        private static readonly HttpClient client = new HttpClient();
        private static readonly SqliteConnection sqlite;
        private const string ConnectionString = "Data Source=transactions.db";

        private const string TOTAL_QUERY = @"
            SELECT 
                SUM(CASE WHEN CAST(Amount as DECIMAL) > 0 THEN Amount ELSE 0 END) as TotalIncome,
                SUM(CASE WHEN CAST(Amount as DECIMAL) < 0 THEN Amount ELSE 0 END) as TotalExpenses
            FROM Transactions";

        private const string CATEGORY_QUERY = @"
            SELECT 
                Category,
                COUNT(*) as TransactionCount,
                SUM(CAST(Amount as DECIMAL)) as TotalAmount
            FROM Transactions
            GROUP BY Category
            ORDER BY ABS(SUM(CAST(Amount as DECIMAL))) DESC";

        static TransactionsController()
        {
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer 24e9164142a8a25b4416fb6a834302c9f9d6164c");
            EnsureDatabaseCreated();
            sqlite = new SqliteConnection(ConnectionString);
        }

        public ActionResult Index() => View();

        public ActionResult FetchAPI()
        {
            try
            {
                using (HttpResponseMessage response = client.GetAsync("https://bank.stuxberg.se/api/iban/SE4550000000058398257466/").Result)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var jsonResult = response.Content.ReadAsStringAsync().Result;
                        var listOfTransactions = JsonSerializer.Deserialize<List<Transaction>>(jsonResult, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }) ?? new List<Transaction>();

                        SaveTransactionsToDatabase(listOfTransactions);
                        return View("DisplayTransactions", listOfTransactions);
                    }
                    else
                    {
                        return View("Error", new ErrorViewModel 
                        { 
                            ErrorMessage = $"API:et svarade med felkod: {response.StatusCode}. Kunde inte hämta transaktioner." 
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return View("Error", new ErrorViewModel 
                { 
                    ErrorMessage = "Kunde inte ansluta till API:et. Kontrollera din internetanslutning eller försök igen senare." 
                });
            }
        }

        private void SaveTransactionsToDatabase(List<Transaction> transactions)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var trans in transactions)
                    {
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
                            }
                            else
                            {
                                categoryToUse = GetCategoryForReference(trans.Reference);
                            }
                        }

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
                        }

                        string checkCategoryQuery = @"
                            SELECT COUNT(*) FROM Categories 
                            WHERE CategoryName = @CategoryName";

                        using (var command = new SqliteCommand(checkCategoryQuery, connection, transaction))
                        {
                            command.Parameters.AddWithValue("@CategoryName", categoryToUse);
                            var count = Convert.ToInt32(command.ExecuteScalar());

                            if (count == 0)
                            {
                                string insertCategoryQuery = @"
                                    INSERT INTO Categories (TransactionID, Reference, CategoryName) 
                                    VALUES (@TransactionID, @Reference, @CategoryName)";

                                using (var command2 = new SqliteCommand(insertCategoryQuery, connection, transaction))
                                {
                                    command2.Parameters.AddWithValue("@TransactionID", trans.TransactionID);
                                    command2.Parameters.AddWithValue("@Reference", trans.Reference);
                                    command2.Parameters.AddWithValue("@CategoryName", categoryToUse);
                                    command2.ExecuteNonQuery();
                                }
                            }
                        }
                    }
                    transaction.Commit();
                }
            }
        }

        public async Task<ActionResult> DisplayTransactions()
        {
            List<Transaction> transactions = new List<Transaction>();
            
            XElement transactionsXml = await GetTransactionsAsXML();
            
            if (transactionsXml.Elements().Any())
            {
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
                return RedirectToAction("FetchAPI");
            }

            return View("DisplayTransactions", transactions);
        }

        private static void EnsureDatabaseCreated()
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();

                string createTransactionsTableCommand = @"
                    CREATE TABLE IF NOT EXISTS Transactions (
                        TransactionID INTEGER PRIMARY KEY,
                        BookingDate TEXT NOT NULL,
                        TransactionDate TEXT NOT NULL,
                        Reference TEXT,
                        Amount REAL NOT NULL,
                        Balance REAL NOT NULL,
                        Category TEXT
                    )";

                using (var command = new SqliteCommand(createTransactionsTableCommand, connection))
                {
                    command.ExecuteNonQuery();
                }

                string createCategoriesTableCommand = @"
                    CREATE TABLE IF NOT EXISTS Categories (
                        ID INTEGER PRIMARY KEY AUTOINCREMENT,
                        TransactionID INTEGER,
                        Reference TEXT,
                        CategoryName TEXT
                    )";

                using (var command = new SqliteCommand(createCategoriesTableCommand, connection))
                {
                    command.ExecuteNonQuery();
                }

                string createRulesTableCommand = @"
                    CREATE TABLE IF NOT EXISTS CategoryRules (
                        RuleID INTEGER PRIMARY KEY AUTOINCREMENT,
                        Reference TEXT NOT NULL,
                        CategoryName TEXT NOT NULL
                    )";

                using (var command = new SqliteCommand(createRulesTableCommand, connection))
                {
                    command.ExecuteNonQuery();
                }

                string checkTableQuery = "SELECT name FROM sqlite_master WHERE type='table' AND name='CategoryRules'";
                using (var command = new SqliteCommand(checkTableQuery, connection))
                {
                    var result = command.ExecuteScalar();
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

        public async Task<IActionResult> Edit()
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

            ViewBag.Transactions = transactions;
            ViewBag.Categories = await GetAllCategories();
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Edit(List<int> selectedTransactions, string category, string newCategory, bool applyToAll)
        {
            if (selectedTransactions == null || selectedTransactions.Count == 0)
            {
                return View("Error", new ErrorViewModel 
                { 
                    ErrorMessage = "Välj minst en transaktion att kategorisera." 
                });
            }

            if (string.IsNullOrEmpty(category) && string.IsNullOrEmpty(newCategory))
            {
                return View("Error", new ErrorViewModel 
                { 
                    ErrorMessage = "Välj en befintlig kategori eller skriv in en ny." 
                });
            }

            string categoryToUse = !string.IsNullOrEmpty(newCategory) ? newCategory : category;

            try
            {
                using (var connection = new SqliteConnection(ConnectionString))
                {
                    await connection.OpenAsync();

                    using (var dbTransaction = connection.BeginTransaction())
                    {
                        try
                        {
                            string getReferenceQuery = "SELECT Reference FROM Transactions WHERE TransactionID = @TransactionID";
                            string reference = null;
                            using (var command = new SqliteCommand(getReferenceQuery, connection, dbTransaction))
                            {
                                command.Parameters.AddWithValue("@TransactionID", selectedTransactions[0]);
                                reference = (string)await command.ExecuteScalarAsync();
                            }

                            if (string.IsNullOrEmpty(reference))
                            {
                                throw new Exception("Kunde inte hitta referens för den valda transaktionen.");
                            }

                            if (applyToAll)
                            {
                                string upsertRuleQuery = @"
                                    INSERT OR REPLACE INTO CategoryRules (Reference, CategoryName) 
                                    VALUES (@Reference, @CategoryName)";
                                
                                using (var command = new SqliteCommand(upsertRuleQuery, connection, dbTransaction))
                                {
                                    command.Parameters.AddWithValue("@Reference", reference);
                                    command.Parameters.AddWithValue("@CategoryName", categoryToUse);
                                    await command.ExecuteNonQueryAsync();
                                }

                                string updateAllQuery = "UPDATE Transactions SET Category = @Category WHERE Reference = @Reference";
                                using (var command = new SqliteCommand(updateAllQuery, connection, dbTransaction))
                                {
                                    command.Parameters.AddWithValue("@Category", categoryToUse);
                                    command.Parameters.AddWithValue("@Reference", reference);
                                    await command.ExecuteNonQueryAsync();
                                }
                            }
                            else
                            {
                                foreach (var transactionId in selectedTransactions)
                                {
                                    string updateSelectedTransactionsQuery = "UPDATE Transactions SET Category = @Category WHERE TransactionID = @TransactionID";
                                    using (var command = new SqliteCommand(updateSelectedTransactionsQuery, connection, dbTransaction))
                                    {
                                        command.Parameters.AddWithValue("@Category", categoryToUse);
                                        command.Parameters.AddWithValue("@TransactionID", transactionId);
                                        await command.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            string checkCategoryQuery = "SELECT COUNT(*) FROM Categories WHERE CategoryName = @CategoryName";
                            using (var command = new SqliteCommand(checkCategoryQuery, connection, dbTransaction))
                            {
                                command.Parameters.AddWithValue("@CategoryName", categoryToUse);
                                var count = Convert.ToInt32(await command.ExecuteScalarAsync());

                                if (count == 0)
                                {
                                    string insertCategoryQuery = "INSERT INTO Categories (CategoryName) VALUES (@CategoryName)";
                                    using (var command2 = new SqliteCommand(insertCategoryQuery, connection, dbTransaction))
                                    {
                                        command2.Parameters.AddWithValue("@CategoryName", categoryToUse);
                                        await command2.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            dbTransaction.Commit();
                            return RedirectToAction(nameof(DisplayTransactions));
                        }
                        catch (Exception ex)
                        {
                            dbTransaction.Rollback();
                            return View("Error", new ErrorViewModel 
                            { 
                                ErrorMessage = "Ett fel uppstod vid uppdatering av kategorier." 
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return View("Error", new ErrorViewModel 
                { 
                    ErrorMessage = "Ett fel uppstod vid anslutning till databasen." 
                });
            }
        }

        private async Task<List<Transaction>> GetAllTransactions()
        {
            var xml = await SQLResult("SELECT * FROM Transactions ORDER BY BookingDate DESC", "Transactions", "Transaction");
            return xml.Elements().Select(element => new Transaction
                {
                    TransactionID = int.Parse(element.Element("TransactionID").Value),
                    Reference = element.Element("Reference").Value,
                    Amount = decimal.Parse(element.Element("Amount").Value, CultureInfo.InvariantCulture),
                    Balance = decimal.Parse(element.Element("Balance").Value, CultureInfo.InvariantCulture),
                    Category = element.Element("Category")?.Value
            }).ToList();
        }

        private List<Rule> GetRules()
        {
            var rules = new List<Rule>();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var command = new SqliteCommand("SELECT RuleID, Reference, CategoryName FROM CategoryRules", connection);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rules.Add(new Rule
                    {
                        RuleID = int.Parse(reader["RuleID"].ToString()),
                        Reference = reader["Reference"].ToString(),
                        CategoryName = reader["CategoryName"].ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ett fel uppstod vid hämtning av regler.");
            }
            return rules;
        }

        private string GetCategoryForReference(string reference)
        {
            var rules = GetRules();
            return rules.FirstOrDefault(r => r.Reference == reference)?.CategoryName ?? "Övrigt";
        }

        public IActionResult Summary()
        {
            try 
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                var accountSummary = new AccountSummary();

                using (var command = new SqliteCommand(@"SELECT 
                    SUM(CASE WHEN Amount > 0 THEN Amount ELSE 0 END) as TotalIncome,
                    SUM(CASE WHEN Amount < 0 THEN ABS(Amount) ELSE 0 END) as TotalExpenses
                    FROM Transactions", connection))
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        accountSummary.TotalIncome = Convert.ToDecimal(reader["TotalIncome"]);
                        accountSummary.TotalExpenses = Math.Abs(Convert.ToDecimal(reader["TotalExpenses"]));
                    }
                }

                using (var command = new SqliteCommand(@"SELECT 
                    COALESCE(Category, 'Övrigt') as Category,
                    COUNT(*) as TransactionCount,
                    SUM(Amount) as TotalAmount
                    FROM Transactions 
                    WHERE Amount > 0
                    GROUP BY Category
                    ORDER BY TotalAmount DESC", connection))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var summary = new TransactionSummary
                        {
                            Category = reader["Category"]?.ToString() ?? "Övrigt",
                            TransactionCount = Convert.ToInt32(reader["TransactionCount"]),
                            TotalAmount = Convert.ToDecimal(reader["TotalAmount"])
                        };
                        accountSummary.Categories.Add(summary);
                    }
                }

                using (var command = new SqliteCommand(@"SELECT 
                    COALESCE(Category, 'Övrigt') as Category,
                    COUNT(*) as TransactionCount,
                    SUM(Amount) as TotalAmount
                    FROM Transactions 
                    WHERE Amount < 0
                    GROUP BY Category
                    ORDER BY ABS(TotalAmount) DESC", connection))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var summary = new TransactionSummary
                        {
                            Category = reader["Category"]?.ToString() ?? "Övrigt",
                            TransactionCount = Convert.ToInt32(reader["TransactionCount"]),
                            TotalAmount = Convert.ToDecimal(reader["TotalAmount"])
                        };
                        accountSummary.Categories.Add(summary);
                    }
                }

                return View(accountSummary);
            }
            catch (Exception)
            {
                return View("Error", new ErrorViewModel 
                { 
                    ErrorMessage = "Ett fel uppstod vid generering av summeringen." 
                });
            }
        }

        public IActionResult DownloadSummary()
        {
            try
            {
                var report = new SummaryReport { GeneratedAt = DateTime.Now };
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                using (var command = new SqliteCommand(@"SELECT 
                    SUM(CASE WHEN Amount > 0 THEN Amount ELSE 0 END) as TotalIncome,
                    SUM(CASE WHEN Amount < 0 THEN ABS(Amount) ELSE 0 END) as TotalExpenses
                    FROM Transactions", connection))
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        report.TotalIncome = Convert.ToDecimal(reader["TotalIncome"]);
                        report.TotalExpenses = Math.Abs(Convert.ToDecimal(reader["TotalExpenses"]));
                    }
                }

                using (var command = new SqliteCommand(@"SELECT 
                    COALESCE(Category, 'Övrigt') as Category,
                    COUNT(*) as TransactionCount,
                    SUM(Amount) as TotalAmount
                    FROM Transactions 
                    WHERE Amount > 0
                    GROUP BY Category
                    ORDER BY TotalAmount DESC", connection))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var summary = new CategorySummary
                        {
                            Category = reader["Category"]?.ToString() ?? "Övrigt",
                            TransactionCount = Convert.ToInt32(reader["TransactionCount"]),
                            TotalAmount = Convert.ToDecimal(reader["TotalAmount"])
                        };
                        report.IncomeCategories.Add(summary);
                    }
                }

                using (var command = new SqliteCommand(@"SELECT 
                    COALESCE(Category, 'Övrigt') as Category,
                    COUNT(*) as TransactionCount,
                    SUM(Amount) as TotalAmount
                    FROM Transactions 
                    WHERE Amount < 0
                    GROUP BY Category
                    ORDER BY ABS(TotalAmount) DESC", connection))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var summary = new CategorySummary
                        {
                            Category = reader["Category"]?.ToString() ?? "Övrigt",
                            TransactionCount = Convert.ToInt32(reader["TransactionCount"]),
                            TotalAmount = Math.Abs(Convert.ToDecimal(reader["TotalAmount"]))
                        };
                        report.ExpenseCategories.Add(summary);
                    }
                }

                var jsonString = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(jsonString);
                return File(bytes, "application/json", "transaction_summary.json");
            }
            catch (Exception)
            {
                return View("Error", new ErrorViewModel 
                { 
                    ErrorMessage = "Ett fel uppstod vid generering av rapporten." 
                });
            }
        }
    }
}
