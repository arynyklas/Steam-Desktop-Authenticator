using System;
using System.IO;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DynamicData.Binding;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using ReactiveUI;
using SteamAuthentication.LogicModels;
using SteamAuthentication.Models;
using TradeOnSda.Data;
using TradeOnSda.ViewModels;
using TradeOnSda.Views.AccountList;
using TradeOnSda.Windows.ImportAccounts;
using TradeOnSda.Windows.NotificationMessage;
using SteamTime = TradeOnSda.Data.SteamTime;

namespace TradeOnSda.Views.Main;

public class MainViewModel : ViewModelBase
{
    private string _steamGuardToken = null!;
    private string _searchText = null!;

    public string SteamGuardToken
    {
        get => _steamGuardToken;
        set => RaiseAndSetIfPropertyChanged(ref _steamGuardToken, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => RaiseAndSetIfPropertyChanged(ref _searchText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => RaiseAndSetIfPropertyChanged(ref _progressValue, value);
    }

    public AccountListViewModel AccountListViewModel { get; }

    public ICommand ImportAccountsCommand { get; }

    public ICommand ReLoginCommand { get; }
    
    public ICommand CopySdaCodeCommand { get; }
    
    public SdaManager SdaManager { get; }

    public bool IsEnabledReLoginButton
    {
        get => _isEnabledReLoginButton;
        set => RaiseAndSetIfPropertyChanged(ref _isEnabledReLoginButton, value);
    }

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly Window _ownerWindow;

    private CancellationTokenSource? _currentSdaCodeCts;
    private double _progressValue;
    private bool _isEnabledReLoginButton;

    public MainViewModel(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
        SteamGuardToken = "-----";
        SearchText = string.Empty;
        ProgressValue = 0d;
        SdaManager = new SdaManager();
        SdaManager.LoadFromDisk();

        AccountListViewModel = new AccountListViewModel(SdaManager, _ownerWindow);

        Observable.Interval(TimeSpan.FromSeconds(0.03))
            .Subscribe(_ =>
            {
                var time = DateTime.UtcNow;
                var date = time.Date;
                
                var delta = (time - date).TotalMilliseconds / 1000d % 30d;

                var value = 100d - delta / 30d * 100d;

                ProgressValue = value;
            });

        this.WhenPropertyChanged(t => t.SearchText)
            .Subscribe(valueWrapper =>
            {
                var newSearchText = valueWrapper.Value;

                AccountListViewModel.SearchText = newSearchText ?? "";
            });
        
        AccountListViewModel
            .WhenPropertyChanged(t => t.SelectedAccountViewModel)
            .Subscribe(valueWrapper =>
                {
                    _currentSdaCodeCts?.Cancel();

                    _currentSdaCodeCts = new CancellationTokenSource();
                    
                    var newValue = valueWrapper.Value;

                    IsEnabledReLoginButton = newValue != null;
                    
                    Task.Run(async () =>
                    {
                        var token = _currentSdaCodeCts.Token;

                        while (true)
                        {
                            if (newValue == null)
                            {
                                SteamGuardToken = "-----";
                                return;
                            }

                            var sdaCode = await newValue.SdaWithCredentials.SteamGuardAccount
                                .TryGenerateSteamGuardCodeForTimeStampAsync(token);

                            SteamGuardToken = sdaCode ?? "-----";

                            token.ThrowIfCancellationRequested();

                            await Task.Delay(1000, token);
                        }
                    });
                }
            );

        ImportAccountsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var result = await _ownerWindow.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("MaFile")
                        {
                            Patterns = new[] { "*.mafile" }
                        }
                    },
                    Title = "Select mafiles",
                });

            foreach (var file in result)
            {
                try
                {
                    var path = file.Path.LocalPath;
                    var maFileName = file.Name;

                    var steamMaFile = JsonConvert.DeserializeObject<SteamMaFile>(await File.ReadAllTextAsync(path))!;

                    await ImportAccountsWindow.CreateImportAccountWindowAsync(
                        steamMaFile.Session.SteamId,
                        steamMaFile.AccountName,
                        maFileName,
                        async (password, proxy, proxyString, sdaSettings) =>
                        {
                            try
                            {
                                var steamTime = new SteamTime();

                                var maFileCredentials =
                                    new MaFileCredentials(proxy != null ? proxyString : null,
                                        password);

                                var sda = new SteamGuardAccount(steamMaFile,
                                    new SteamRestClient(new HttpClient(), proxy),
                                    steamTime,
                                    NullLogger<SteamGuardAccount>.Instance);

                                var loginAgainResult = await sda.LoginAgainAsync(steamMaFile.AccountName, password);

                                if (loginAgainResult != LoginResult.LoginOkay)
                                    return false;

                                await SdaManager.AddAccountAsync(sda, maFileCredentials, sdaSettings);

                                return true;
                            }
                            catch (Exception)
                            {
                                return false;
                            }
                        },
                        _ownerWindow, SdaManager);
                }
                catch (Exception e)
                {
                    await NotificationsMessageWindow.ShowWindow($"Invalid mafile. Error: {e.Message}", _ownerWindow);
                }
            }
        });

        ReLoginCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var selectedAccountViewModel = AccountListViewModel.SelectedAccountViewModel;
            
            if (selectedAccountViewModel == null)
                return;

            try
            {
                var credentials = selectedAccountViewModel.SdaWithCredentials.Credentials;
                var username = selectedAccountViewModel.SdaWithCredentials.SteamGuardAccount.MaFile.AccountName;

                var result =
                    await selectedAccountViewModel.SdaWithCredentials.SteamGuardAccount.LoginAgainAsync(username,
                        credentials.Password);

                if (result != LoginResult.LoginOkay)
                {
                    await NotificationsMessageWindow.ShowWindow("Error login in steam", _ownerWindow);
                    return;
                }

                await SdaManager.SaveSettingsAsync();
            }
            catch (Exception)
            {
                await NotificationsMessageWindow.ShowWindow("Error login in steam", _ownerWindow);
            }
        });

        CopySdaCodeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SteamGuardToken == "-----")
                return;
            
            var setTask = _ownerWindow.Clipboard?.SetTextAsync(SteamGuardToken);

            if (setTask != null)
                await setTask;
        });
    }

    public MainViewModel()
    {
        _ownerWindow = null!;
        SteamGuardToken = "AS2X3";
        SearchText = string.Empty;
        ImportAccountsCommand = null!;
        AccountListViewModel = null!;
        SdaManager = null!;
        ProgressValue = 0d;
        ReLoginCommand = null!;
        CopySdaCodeCommand = null!;
    }
}