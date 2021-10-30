using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using DevExpress.Xpf.Charts;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace BalanceBrowser
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly IDataStore DataStore;
        private readonly Random Random = new();

        public MainWindow(IDataStore store)
        {
            InitializeComponent();
            DataStore = store;
            CreateDb();
            for (int i = 1; i < 501; i++)
            {
                ChartSelector.Items.Add(i);
            }

            ChartSelector.EditValueChanged += ChartSelectorEditValueChanged;
        }

        private void ChartSelectorEditValueChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
            Diagram.Series.Clear();
            Diagram.Series.Add(new LineSeries2D() { Name = "Data" });
            LineSeries2D series = Diagram.Series[0] as LineSeries2D;
            if (series is null)
            {
                return;
            }

            AccountWInfo? a = DataStore.FindByName($"Test{e.NewValue}");
            if (a is null)
            {
                return;
            }

            List<Transaction>? txs = Transactions.ForAccount(DataStore, a.Id);
            if (txs is null)
            {
                return;
            }

            if (txs.Count == 0)
            {
                Diagram.Series.Clear();
                return;
            }
            decimal currVal = a.StartBalance;
            DateTime start = txs.Min(t => t.When);
            txs.ForEach(t =>
            {
                currVal += t.Amount;
                _ = series.AddPoint(t.When - start, (double)currVal);
            });
        }

        private void CreateDb()
        {
            List<AccountWInfo> accounts = new();
            for (int x = 0; x < 500; x++)
            {
                Account acct = new();
                AccountInfo info = new() { Name = $"Test{x}" };
                acct.IsDebitAccount = Random.Next(0, 2) > 0;
                accounts.Add(new AccountWInfo(acct, info));
            }
            DataStore.BulkInsert(accounts);
            DateTime when = DateTime.UtcNow;
            List<Transaction> txs = new(1000000);
            for (int x = 0; x < 1000000; x++)
            {
                int creditAcct = Random.Next(0, 500);
                int debitAcct = Random.Next(0, 500);
                while (debitAcct == creditAcct)
                {
                    debitAcct = Random.Next(0, 500);
                }

                when = when.AddMilliseconds(1);
                AccountWInfo? credAcct = accounts.Find(a => a.Info.Name == $"Test{creditAcct}");
                AccountWInfo? debAcct = accounts.Find(a => a.Info.Name == $"Test{debitAcct}");
                Transaction tx = new(credAcct?.Id ?? Guid.Empty, debAcct?.Id ?? Guid.Empty, Random.Next(-100, 100), when);
                txs.Add(tx);
                if (x % 1000 == 0)
                {
                    Debug.WriteLine(x);
                }
            }
            Debug.WriteLine("Inserting");
            List<Transaction>? result = Transactions.BulkInsert(DataStore, txs);
            Debug.WriteLine($"{result.Count} failed");
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            DataStore.Dispose();
            File.Delete(@"C:\Temp\Accounts.db");
        }
    }
}