﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Downloader;
using HandyControl.Controls;
using HandyControl.Tools;
using HandyWinget.Common;
using HandyWinget.Common.Models;
using HandyWinget.Control;
using Microsoft.VisualBasic;
using Microsoft.Win32;
using ModernWpf.Controls;
using static HandyControl.Tools.DispatcherHelper;
using static HandyWinget.Common.DatabaseOperation;
using static HandyWinget.Common.Helper;
using IconElement = HandyControl.Controls.IconElement;
using TabItem = HandyControl.Controls.TabItem;

namespace HandyWinget.Views
{
    public partial class PackageView : UserControl
    {
        private bool hasLoaded = false;
        private bool hasViewLoaded = false;
        private bool hasStarted = false;
        ICollectionView view;
        ICollectionView viewInstalled;
        List<string> openedPackages = new List<string>();
        public PackageView()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += PackageView_Loaded;
            InitSettings();
        }

        private void InitSettings()
        {
            txtUpdateDate.Text = $"Last Update: {Settings.UpdatedDate}";
        }

        private void PackageView_Loaded(object sender, RoutedEventArgs e)
        {
            // if the view is recreated (switch between views), the information will not be received again
            if (!hasViewLoaded)
            {
                hasViewLoaded = true;

                // if indexVx.db not found or AutoRefresh is True we should Download MSIX otherwise we Load Database
                if (!File.Exists(Consts.HWGDatabasePath) || Settings.AutoRefreshInStartup)
                {
                    btnUpdate_Click(null, null);
                }
                else
                {
                    LoadDatabaseAsync();
                }

                // set Datagrid Columns Width from Settings
                if (Settings.IsStoreDataGridColumnWidth)
                {
                    if (Settings.DataGridColumnWidth.Count > 0)
                    {
                        for (var i = 0; i < dataGrid.Columns.Count; i++)
                        {
                            dataGrid.Columns[i].Width = Settings.DataGridColumnWidth[i];
                        }
                    }

                    if (Settings.DataGridInstalledColumnWidth.Count > 0)
                    {
                        for (var i = 0; i < dataGridInstalled.Columns.Count; i++)
                        {
                            dataGridInstalled.Columns[i].Width = Settings.DataGridInstalledColumnWidth[i];
                        }
                    }

                    hasLoaded = true;
                }

                CheckLastTimeDatabaseUpdate();
            }
        }

        /// <summary>
        /// Display a notification if 24 hours have elapsed since the last update
        /// </summary>
        private void CheckLastTimeDatabaseUpdate()
        {
            var lastDT = Settings.UpdatedDate;
            var currentDT = DateTime.Now;
            TimeSpan duration = currentDT - lastDT;
            if (duration.TotalHours >= 24)
            {
                CreateInfoBar("Update Database", $"You last time updated the database in {lastDT}. To get newer packages, please update the packages.", panel, Severity.Warning);
            }

        }

        /// <summary>
        /// Group DataGrid based on Publisher
        /// </summary>
        private void SetGroupDataGrid()
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(dataGrid.ItemsSource);

            if (Settings.GroupByPublisher)
            {
                if (view != null)
                {
                    view.GroupDescriptions.Clear();
                    view.GroupDescriptions.Add(new PropertyGroupDescription("Publisher"));
                }
            }
            else
            {
                if (view != null)
                {
                    view.GroupDescriptions.Clear();
                }
            }
        }

        #region Download MSIX
        /// <summary>
        /// Download MSIX from Azure
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void btnUpdate_Click(object sender, RoutedEventArgs e)
        {
            btnUpdate.IsEnabled = false;
            bool isConnected = ApplicationHelper.IsConnectedToInternet();
            if (isConnected)
            {
                txtStatus.Text = "Downloading Database...";
                txtMSIXStatus.Text = string.Empty;
                prgMSIX.Value = 0;
                prgMSIX.Visibility = Visibility.Visible;
                prgMSIX.IsIndeterminate = false;
                var downloader = new DownloadService();
                downloader.DownloadProgressChanged += Downloader_DownloadProgressChanged;
                downloader.DownloadFileCompleted += Downloader_DownloadFileCompleted;
                await downloader.DownloadFileTaskAsync(Consts.MSIXSourceUrl, new DirectoryInfo(Consts.MSIXPath));
            }
            else
            {
                CreateInfoBar("Network UnAvailable", "Unable to connect to the Internet", panel, Severity.Error);
                btnUpdate.IsEnabled = true;
            }
        }

        /// <summary>
        /// Extract MSIX into indexVx.db
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Downloader_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                RunOnMainThread(() =>
                {
                    prgMSIX.IsIndeterminate = false;
                    prgMSIX.Visibility = Visibility.Collapsed;
                    btnUpdate.IsEnabled = true;
                    CreateInfoBar("Error", e.Error.Message, panel, Severity.Error);
                });
            }
            else
            {
                var downloadInfo = e.UserState as DownloadPackage;
                if (downloadInfo != null && downloadInfo.FileName != null)
                {
                    RunOnMainThread(() =>
                    {
                        txtStatus.Text = "Extracting...";
                        prgMSIX.IsIndeterminate = true;
                        ZipFile.ExtractToDirectory(downloadInfo.FileName, Consts.MSIXPath, true);
                    });
                    await Task.Run(() =>
                    {
                        GenerateDatabaseAsync();

                    }).ContinueWith(x =>
                    {
                        RunOnMainThread(() =>
                        {
                            prgMSIX.IsIndeterminate = false;
                            prgMSIX.Visibility = Visibility.Collapsed;
                            Settings.UpdatedDate = DateTime.Now;
                            txtUpdateDate.Text = $"Last Update: {DateTime.Now}";
                            btnUpdate.IsEnabled = true;
                        });
                        LoadDatabaseAsync();
                    });
                }
            }
        }

        private void Downloader_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            var value = (int) e.ProgressPercentage;
            RunOnMainThread(() =>
            {
                prgMSIX.Value = value;
                txtMSIXStatus.Text = $"Downloading {BytesToMegabytes(e.ReceivedBytesSize)} MB of {BytesToMegabytes(e.TotalBytesToReceive)} MB  -  {value}%";
            });
        }

        /// <summary>
        /// Load Packages to DataGrid and Identify Installed packages
        /// </summary>
        private async void LoadDatabaseAsync()
        {
            var list = await GetAllPackageAsync();
            RunOnMainThread(() =>
            {
                dataGrid.ItemsSource = list;
            });

            SetGroupDataGrid();

            IdentifyInstalledPackages(list);
        }

        #endregion

        #region Identify Installed Packages

        /// <summary>
        /// Identify Installed Packages
        /// </summary>
        private async void IdentifyInstalledPackages(IEnumerable<HWGPackageModel> list)
        {
            if (!hasStarted)
            {
                if (Settings.IdentifyInstalledPackage)
                {
                    if (!IsWingetInstalled())
                    {
                        RunOnMainThread(() =>
                        {
                            CreateInfoBarWithAction("Winget-Cli", "We need Winget-cli version 1.0 or higher to identify packages, Please download and install it first then restart HandyWinget.", panelInstalled, Severity.Error, "Download", () =>
                            {
                                StartProcess(Consts.WingetRepository);
                            });
                        });
                    }
                    else
                    {
                        RunOnMainThread(() =>
                        {
                            prgInstalled.Visibility = Visibility.Visible;
                        });

                        var value = new Progress<int>(ReportProgress);
                        hasStarted = true;
                        await Task.Run(() =>
                        {
                            LoadInstalledList(value, list);
                        });
                    }
                }
                else
                {
                    RunOnMainThread(() =>
                    {
                        CreateInfoBarWithAction("Note", "You have disabled package identification in settings, go to Settings and enable it (To be effective, you must restart HandyWinget). Note that activating this feature will reduce the loading speed.", panelInstalled, Severity.Warning, "Settings", () =>
                        {
                            MainWindow.Instance.navView.SelectedItem = MainWindow.Instance.navView.MenuItems[0] as NavigationViewItem;
                        });
                    });
                }
            }
        }

        void ReportProgress(int value)
        {
            RunOnMainThread(() =>
            {
                prgInstalled.Value = value;
            });
        }

        /// <summary>
        /// Load Installed Packages into Datagrid 
        /// </summary>
        /// <param name="progress"></param>
        private void LoadInstalledList(IProgress<int> progress, IEnumerable<HWGPackageModel> list)
        {
            var installedList = new ThreadSafeObservableCollection<HWGInstalledPackageModel>();
            var installedAppList = GetInstalledAppList(); // pure strings array

            if (installedAppList == null)
            {
                // HandyControl UI method
                RunOnMainThread(() =>
                {
                    CreateInfoBarWithAction(
                        "Update Winget-Cli",
                        "your Winget-cli is not supported please Update your winget-cli to version 1.0 or higher.",
                        panelInstalled,
                        Severity.Error,
                        "Update",
                        () =>{StartProcess(Consts.WingetRepository);});
                });
                return;
            }
            var allPackages = list;
            var allPackagesCount = allPackages.Count();
            int currentItemIndex = 0;
            // FIXME: too much loops, need change outer and inner
            foreach (var installedItem in installedAppList)
                //foreach (var package in allPackages)
            {
                currentItemIndex += 1;
                progress.Report((currentItemIndex * 100 / installedAppList.Count()));
                foreach (var package in allPackages)
                //foreach (var installedItem in installedAppList)
                {
                    var installedApp = ParseInstalledApp(installedItem, package.PackageId);
                    // FIXME: app must have packageId or version? 
                    if (installedApp.packageId != null && installedApp.version != null)
                    {
                        if (package.PackageId.Equals(installedApp.packageId, StringComparison.OrdinalIgnoreCase))
                        {
                            installedList.Add(new HWGInstalledPackageModel
                            {
                                Name = package.Name,
                                PackageId = package.PackageId,
                                Publisher = package.Publisher,
                                ProductCode = package.ProductCode,
                                YamlUri = package.YamlUri,
                                Version = installedApp.version,
                                AvailableVersion = installedApp.availableVersion
                            });
                            break;
                        }
                    }
                }
            }

            RunOnMainThread(() =>
            {
                prgInstalled.Visibility = Visibility.Collapsed;
                dataGridInstalled.ItemsSource = installedList;
            });
        }
        #endregion

        #region Filter DataGrid
        private void AutoSuggestBox_OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (!string.IsNullOrEmpty(autoBox.Text))
            {
                view = CollectionViewSource.GetDefaultView(dataGrid.ItemsSource);
                if (view == null)
                    return;
                view.Filter = new Predicate<object>(filterPackages);
            }
            view?.Refresh();
            var suggestions = new List<string>();
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                foreach (var item in view)
                {
                    suggestions.Add((item as HWGPackageModel).Name);
                }

                if (suggestions.Count > 0)
                {
                    for (int i = 0; i < suggestions.Count; i++)
                    {
                        autoBox.ItemsSource = suggestions;
                    }
                }
                else
                {
                    autoBox.ItemsSource = new string[] { "No result found" };
                }
            }
        }
        private void autoBoxInstalled_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (!string.IsNullOrEmpty(autoBoxInstalled.Text))
            {
                viewInstalled = CollectionViewSource.GetDefaultView(dataGridInstalled.ItemsSource);
                if (viewInstalled == null)
                    return;
                viewInstalled.Filter = new Predicate<object>(filterInstalledPackages);
            }
            viewInstalled?.Refresh();
            var suggestions = new List<string>();
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                foreach (var item in viewInstalled)
                {
                    suggestions.Add((item as HWGInstalledPackageModel).Name);
                }

                if (suggestions.Count > 0)
                {
                    for (int i = 0; i < suggestions.Count; i++)
                    {
                        autoBoxInstalled.ItemsSource = suggestions;
                    }
                }
                else
                {
                    autoBoxInstalled.ItemsSource = new string[] { "No result found" };
                }
            }
        }
        private bool filterPackages(object item)
        {
            var filter = item as HWGPackageModel;
            if (filter.PackageId.Contains(autoBox.Text, StringComparison.OrdinalIgnoreCase) ||
                filter.Name.Contains(autoBox.Text, StringComparison.OrdinalIgnoreCase) ||
                filter.Publisher.Contains(autoBox.Text, StringComparison.OrdinalIgnoreCase))
            {

                return true;
            }
            return false;
        }

        private bool filterInstalledPackages(object item)
        {
            var filter = item as HWGInstalledPackageModel;
            if (filter.PackageId.Contains(autoBoxInstalled.Text, StringComparison.OrdinalIgnoreCase) ||
                filter.Name.Contains(autoBoxInstalled.Text, StringComparison.OrdinalIgnoreCase) ||
                filter.Publisher.Contains(autoBoxInstalled.Text, StringComparison.OrdinalIgnoreCase))
            {

                return true;
            }
            return false;
        }

        #endregion

        #region DataGrid Layout Updated
        private void dataGrid_LayoutUpdated(object sender, EventArgs e)
        {
            if (!hasLoaded)
                return;
            if (Settings.IsStoreDataGridColumnWidth)
            {
                for (int i = Settings.DataGridColumnWidth.Count; i < dataGrid.Columns.Count; i++)
                {
                    Settings.DataGridColumnWidth.Add(default);
                }

                for (int index = 0; index < dataGrid.Columns.Count; index++)
                {
                    if (dataGrid.Columns == null)
                        return;
                    Settings.DataGridColumnWidth[index] = new DataGridLength(dataGrid.Columns[index].ActualWidth);
                }
            }
        }

        private void dataGridInstalled_LayoutUpdated(object sender, EventArgs e)
        {
            if (!hasLoaded)
                return;
            if (Settings.IsStoreDataGridColumnWidth)
            {
                for (int i = Settings.DataGridInstalledColumnWidth.Count; i < dataGridInstalled.Columns.Count; i++)
                {
                    Settings.DataGridInstalledColumnWidth.Add(default);
                }

                for (int index = 0; index < dataGridInstalled.Columns.Count; index++)
                {
                    if (dataGridInstalled.Columns == null)
                        return;
                    Settings.DataGridInstalledColumnWidth[index] = new DataGridLength(dataGridInstalled.Columns[index].ActualWidth);
                }
            }
        }

        #endregion

        #region ContextMenu
        private void DataGridContextMenu_Loaded(object sender, RoutedEventArgs e)
        {
            var selectedRowsCount = dataGrid.SelectedItems.Count;

            if (selectedRowsCount > 1)
            {
                mnuCopyScript.IsEnabled = false;
                mnuSendToCmd.IsEnabled = false;
            }
            else
            {
                mnuCopyScript.IsEnabled = true;
                mnuSendToCmd.IsEnabled = true;
            }
        }

        private void DataGridContextMenu_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is MenuItem button)
            {
                DataGridContextMenuActions(button.Tag.ToString());
            }
        }

        private void DataGridContextMenuActions(string tag)
        {
            var selectedRowsCount = dataGrid.SelectedItems.Count;
            var package = dataGrid.SelectedItem as HWGPackageModel;

            string text = $"winget install {package.PackageId} -v {package.PackageVersion.Version}";
            switch (tag)
            {
                case "SendToPow":
                    if (selectedRowsCount > 1)
                    {
                        var script = CreatePowerShellScript(false);
                        Process.Start("powershell.exe", script);
                    }
                    else if (selectedRowsCount == 1)
                    {
                        Process.Start("powershell.exe", text);
                    }
                    break;
                case "SendToCmd":
                    if (selectedRowsCount == 1)
                    {
                        Interaction.Shell(text, AppWinStyle.NormalFocus);
                    }
                    break;
                case "Copy":
                    if (selectedRowsCount == 1)
                    {
                        Clipboard.SetText(text);
                    }
                    break;
                case "Export":
                    ExportPowerShellScript();
                    break;
            }

        }

        private void DataGridInstalledContextMenu_Loaded(object sender, RoutedEventArgs e)
        {
            var selectedRowsCount = dataGridInstalled.SelectedItems.Count;
            var selectedRow = dataGridInstalled.SelectedItem as HWGInstalledPackageModel;

            if (selectedRowsCount > 1)
            {
                mnuUpgrade.IsEnabled = false;
                mnuUninstall.IsEnabled = false;
                mnuInstalledCopyScript.IsEnabled = false;
            }
            else
            {
                mnuUpgrade.IsEnabled = true;
                mnuUninstall.IsEnabled = true;
                mnuInstalledCopyScript.IsEnabled = true;
            }

            if (!IsWingetInstalled())
            {
                mnuUpgrade.IsEnabled = false;
                mnuUninstall.IsEnabled = false;
            }
            if (string.IsNullOrEmpty(selectedRow.AvailableVersion) || selectedRowsCount > 1)
            {
                mnuUpgrade.IsEnabled = false;
            }
        }
        private void DataGridInstalledContextMenu_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is MenuItem button)
            {
                DataGridInstalledContextMenuActions(button.Tag.ToString());
            }
        }
        private async void DataGridInstalledContextMenuActions(string tag)
        {
            var selectedRowsCount = dataGridInstalled.SelectedItems.Count;
            var package = dataGridInstalled.SelectedItem as HWGInstalledPackageModel;

            string text = $"winget install {package.PackageId} -v {package.Version}";
            switch (tag)
            {
                case "Copy":
                    if (selectedRowsCount == 1)
                    {
                        Clipboard.SetText(text);
                    }
                    break;
                case "Export":
                    ExportPowerShellScript(true);
                    break;
                case "Upgrade":
                    if (selectedRowsCount == 1 && !string.IsNullOrEmpty(package.AvailableVersion))
                    {
                        var result = false;
                        mnuUpgrade.IsEnabled = false;
                        await Task.Run(() =>
                        {
                            result = UpgradePackage(package.PackageId);
                        });
                        if (result)
                        {
                            CreateInfoBar("Upgrade", $"Your Selected Package ({package.PackageId}) Successfully Upgraded", panelInstalled, Severity.Success);
                        }
                        else
                        {
                            CreateInfoBar("Upgrade", $"We Cant Upgrade Your Selected Package ({package.PackageId})", panelInstalled, Severity.Error);
                        }
                    }
                    else
                    {
                        CreateInfoBar("Upgrade", $"Your Selected Package ({package.PackageId}) does not have any Available Version", panelInstalled, Severity.Error);
                    }
                    mnuUpgrade.IsEnabled = true;

                    break;
                case "Uninstall":
                    if (selectedRowsCount == 1 && !string.IsNullOrEmpty(package.ProductCode))
                    {
                        var result = false;
                        mnuUninstall.IsEnabled = false;
                        await Task.Run(() =>
                        {
                            result = UninstallPackage(package.ProductCode);
                        });
                        if (result)
                        {
                            CreateInfoBar("Uninstall", $"Your Selected Package ({package.PackageId}) Successfully Uninstalled", panelInstalled, Severity.Success);
                        }
                        else
                        {
                            CreateInfoBar("Uninstall", $"We Cant Uninstall Your Selected Package ({package.PackageId})", panelInstalled, Severity.Error);
                        }
                    }
                    else
                    {
                        CreateInfoBar("Uninstall", $"Your Selected Package ({package.PackageId}) does not have a productCode", panelInstalled, Severity.Error);
                    }
                    mnuUninstall.IsEnabled = true;

                    break;
            }
        }

        public async void ExportPowerShellScript(bool isInstalled = false)
        {
            var selectedRowsCount = isInstalled ? dataGridInstalled.SelectedItems.Count : dataGrid.SelectedItems.Count;
            if (selectedRowsCount > 0)
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Save Script",
                    FileName = "winget-script.ps1",
                    DefaultExt = "ps1",
                    Filter = "Powershell Script (*.ps1)|*.ps1"
                };
                if (dialog.ShowDialog() == true)
                {
                    if (isInstalled)
                    {
                        await File.WriteAllTextAsync(dialog.FileName, CreatePowerShellScript(true, true));
                    }
                    else
                    {
                        await File.WriteAllTextAsync(dialog.FileName, CreatePowerShellScript(true));
                    }
                }
            }
        }

        private string CreatePowerShellScript(bool isExportScript, bool isInstalled = false)
        {
            StringBuilder builder = new StringBuilder();
            if (isExportScript)
            {
                builder.Append(Helper.PowerShellScript);
            }

            if (isInstalled)
            {
                foreach (var item in dataGridInstalled.SelectedItems)
                {
                    builder.Append($"winget install {((HWGInstalledPackageModel) item).PackageId} -v {((HWGInstalledPackageModel) item).Version} -e ; ");
                }
            }
            else
            {
                foreach (var item in dataGrid.SelectedItems)
                {
                    builder.Append($"winget install {((HWGPackageModel) item).PackageId} -v {((HWGPackageModel) item).PackageVersion.Version} -e ; ");
                }
            }

            builder.Remove(builder.ToString().LastIndexOf(";"), 1);
            if (isExportScript)
            {
                builder.AppendLine("}");
            }

            return builder.ToString().TrimEnd();
        }

        #endregion

        private void UserControl_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.LeftShift) && e.Key == Key.P)
            {
                DataGridContextMenuActions("SendToPow");
            }
            else if (Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.LeftShift) && e.Key == Key.W)
            {
                DataGridContextMenuActions("SendToCmd");
            }
            else if (Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.LeftShift) && e.Key == Key.C)
            {
                DataGridContextMenuActions("Copy");
            }
            else if (Keyboard.IsKeyDown(Key.LeftCtrl) && e.Key == Key.U)
            {
                DataGridContextMenuActions("Uninstall");
            }
            else if (Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.LeftShift) && e.Key == Key.X)
            {
                DataGridContextMenuActions("Export");
            }
            else if (Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.LeftShift) && e.Key == Key.U)
            {
                DataGridInstalledContextMenuActions("Upgrade");
            }
            else if (Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.LeftShift) && e.Key == Key.I)
            {
                DataGridInstalledContextMenuActions("Copy");
            }
            else if (Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.LeftShift) && e.Key == Key.E)
            {
                DataGridInstalledContextMenuActions("Export");
            }
        }

        #region Get Manifests
        private void GetYamlLink(bool isInstalled = false)
        {
            if (isInstalled)
            {
                foreach (var item in dataGridInstalled.SelectedItems)
                {
                    var selectedRow = item as HWGInstalledPackageModel;
                    var yamlLink = $"{Consts.AzureBaseUrl}{selectedRow.YamlUri}";
                    var header = $"{selectedRow.Name}-{selectedRow.Version}";
                    if (!openedPackages.Any(x => x.Equals(header + "installed")))
                    {
                        CreateTabItem(header, yamlLink, null, true);
                    }
                }
            }
            else
            {
                foreach (var item in dataGrid.SelectedItems)
                {
                    var selectedRow = item as HWGPackageModel;
                    var yamlLink = $"{Consts.AzureBaseUrl}{selectedRow.PackageVersion.YamlUri}";
                    var header = $"{selectedRow.Name}-{selectedRow.PackageVersion.Version}";
                    if (!openedPackages.Any(x => x.Equals(header)))
                    {
                        CreateTabItem(header, yamlLink, selectedRow.Versions);
                    }
                }
            }
        }
        private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Settings.IsShowDetailByDoubleClick)
            {
                var currentDataGrid = GetCurrentActiveDataGrid();

                if (currentDataGrid.SelectedItems.Count == 1)
                {
                    if (mainTab.SelectedIndex == 1)
                    {
                        GetYamlLink(true);
                    }
                    else
                    {
                        GetYamlLink();
                    }
                    mainTabItemDetail.IsEnabled = true;
                    mainTab.SelectedIndex = 2;
                }
            }
            e.Handled = true;
        }
        private void dataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            //// Have to do this in the unusual case where the border of the cell gets selected.
            //// and causes a crash 'EditItem is not allowed'
            e.Cancel = true;
        }

        private void CreateTabItem(string header, string yamlLink, List<PackageVersion> versions, bool isInstalled = false)
        {
            if (string.IsNullOrEmpty(header) && string.IsNullOrEmpty(yamlLink))
            {
                return;
            }
            var tabItem = new TabItem();
            tabItem.Header = header;
            tabItem.Closing += (s, e) =>
            {
                var currentTabItem = s as TabItem;
                if (tabItemPackage.Items.Count == 1)
                {
                    tabItemPackage.Visibility = Visibility.Collapsed;
                    mainTab.SelectedIndex = 0;
                    openedPackages.Clear();
                }
                openedPackages.Remove(mainTab.SelectedIndex == 1 ? currentTabItem.Header.ToString() + "installed" : currentTabItem.Header.ToString());
            };
            openedPackages.Add(isInstalled ? header + "installed" : header);
            tabItem.Content = new PackageDetailView(yamlLink, versions, isInstalled);
            if (isInstalled)
            {
                IconElement.SetGeometry(tabItem, ResourceHelper.GetResource<Geometry>("InstalledGeometry"));
                IconElement.SetWidth(tabItem, 22);
                IconElement.SetHeight(tabItem, 22);
            }
            tabItemPackage.Items.Add(tabItem);
            tabItemPackage.Visibility = Visibility.Visible;
            tabItemPackage.SelectedIndex = tabItemPackage.Items.Count - 1;
        }

        #endregion

        private void btnGoToDetail_Click(object sender, RoutedEventArgs e)
        {
            if (mainTab.SelectedIndex == 1)
            {
                if (dataGridInstalled.SelectedItems.Count == 0)
                {
                    return;
                }
                GetYamlLink(true);
            }
            else
            {
                if (dataGrid.SelectedItems.Count == 0)
                {
                    return;
                }
                GetYamlLink();
            }
            mainTabItemDetail.IsEnabled = true;
            mainTab.SelectedIndex = 2;
        }

        private DataGrid GetCurrentActiveDataGrid()
        {
            if (mainTab.SelectedIndex == 1)
            {
                return dataGridInstalled;
            }
            else
            {
                return dataGrid;
            }
        }
    }
}
