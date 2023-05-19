﻿// Copyright (c) Microsoft Corporation and Contributors
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHome.Common.Extensions;
using DevHome.Common.Services;
using DevHome.Common.TelemetryEvents.SetupFlow;
using DevHome.SetupFlow.Common.Helpers;
using DevHome.SetupFlow.Models;
using DevHome.SetupFlow.Services;
using DevHome.Telemetry;
using Microsoft.UI.Xaml;
using Microsoft.Windows.DevHome.SDK;
using static DevHome.SetupFlow.Models.Common;

namespace DevHome.SetupFlow.ViewModels;

/// <summary>
/// View model to handle the top layer of the repo tool including
/// 1. Repo Review
/// 2. Switching between account, repositories, and url page
/// </summary>
public partial class AddRepoViewModel : ObservableObject
{
    private readonly ISetupFlowStringResource _stringResource;

    private readonly List<CloningInformation> _previouslySelectedRepos;

    /// <summary>
    /// Gets or sets the list that keeps all repositories the user wants to clone.
    /// </summary>
    public List<CloningInformation> EverythingToClone
    {
        get; set;
    }

    /// <summary>
    /// The url of the repository the user wants to clone.
    /// </summary>
    [ObservableProperty]
    private string _url = string.Empty;

    /// <summary>
    /// All the providers Dev Home found.  Used for logging in the accounts and getting all repositories.
    /// </summary>
    private RepositoryProviders _providers;

    /// <summary>
    /// The list of all repositories shown to the user on the repositories page.
    /// </summary>
    private IEnumerable<IRepository> _repositoriesForAccount;

    /// <summary>
    /// Names of all providers.  This is shown to the user on the accounts page.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string> _providerNames = new ();

    /// <summary>
    /// Names of all accounts the user has logged into for a particular provider.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccountComboBoxEnabled))]
    private ObservableCollection<string> _accounts = new ();

    /// <summary>
    /// The currently selected account.
    /// </summary>
    private string _selectedAccount;

    /// <summary>
    /// All repositories currently shown on the screen.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<RepoViewListItem> _repositories = new ();

    /// <summary>
    /// Should the URL page be visible?
    /// </summary>
    [ObservableProperty]
    private Visibility _showUrlPage;

    /// <summary>
    /// Should the account page be visible?
    /// </summary>
    [ObservableProperty]
    private Visibility _showAccountPage;

    /// <summary>
    /// Should the repositories page be visible?
    /// </summary>
    [ObservableProperty]
    private Visibility _showRepoPage;

    /// <summary>
    /// Should the error text be shown?
    /// </summary>
    [ObservableProperty]
    private Visibility _showErrorTextBox;

    /// <summary>
    /// Keeps track of if the account button is checked.  Used to switch UIs
    /// </summary>
    [ObservableProperty]
    private bool? _isAccountToggleButtonChecked;

    /// <summary>
    /// Keeps track if the URL button is checked.  Used to switch UIs
    /// </summary>
    [ObservableProperty]
    private bool? _isUrlAccountButtonChecked;

    /// <summary>
    /// Controls if the primary button is enabled.  Turns true if everything is correct.
    /// </summary>
    [ObservableProperty]
    private bool _shouldPrimaryButtonBeEnabled;

    [ObservableProperty]
    private string _primaryButtonText;

    [ObservableProperty]
    private string _urlParsingError;

    public bool IsAccountComboBoxEnabled => Accounts.Count > 1;

    [ObservableProperty]
    private Visibility _shouldShowUrlError;

    /// <summary>
    /// Indicates if the ListView is currently filtering items.  An unfortunant result of manually filtering a list view
    /// is that the SelectionChanged is fired for any selected item that is removed and the item isn't "re-selected"
    /// To prevent our EverythingToClone from changing this flag is used.
    /// If true any removals caused by filtering are ignored.
    /// Question.  If the items aren't "re-selected" how do they become selected?  The list view has SelectRange
    /// that can be used to re-select items.  This is done in the view.
    /// </summary>
    private bool _isFiltering;

    /// <summary>
    /// Gets or sets a value indicating whether the SelectionChange event fired because SelectRange was called.
    /// After filtering SelectRange is called to re-select all previously selected items.  This causes SelectionChanged
    /// to be fired for each item.  Because EverythingToClone didn't change during filtering it contains every item to select.
    /// This flag is to prevent adding duplicate items are being re-selected.
    /// </summary>
    public bool IsCallingSelectRange { get; set; }

    /// <summary>
    /// Filters all repos down to any that start with text.
    /// A side-effect of filtering is that SelectionChanged fires for every selected repo but only on removal.
    /// SelectionChanged isn't fired for re-adding.  To prevent the RepoTool from forgetting the repos that were selected
    /// the flag _isFiltering is used to prevent modifications to EverythingToClone.
    /// Once filtering is done SelectRange is called on each item in EverythingToClone to re-select them.
    /// </summary>
    /// <param name="text">The text to use with .StartsWith</param>
    public void FilterRepositories(string text)
    {
        IEnumerable<IRepository> filteredRepositories;
        if (text.Equals(string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            filteredRepositories = _repositoriesForAccount;
        }
        else
        {
            filteredRepositories = _repositoriesForAccount
                .Where(x => x.DisplayName.StartsWith(text, StringComparison.OrdinalIgnoreCase));
        }

        _isFiltering = true;
        Repositories = new ObservableCollection<RepoViewListItem>(OrderRepos(filteredRepositories));
        _isFiltering = false;
    }

    /// <summary>
    /// Order repos in a particular order.  The order is
    /// 1. User Private repos
    /// 2. Org repos
    /// 3. User Public repos.
    /// Each section is ordered by the most recently updated.
    /// </summary>
    /// <param name="repos">The list of repos to order.</param>
    /// <returns>An enumerable collection of items ready to be put into the ListView</returns>
    private IEnumerable<RepoViewListItem> OrderRepos(IEnumerable<IRepository> repos)
    {
        var organizationRepos = repos.Where(x => !x.OwningAccountName.Equals(_selectedAccount, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.LastUpdated)
            .Select(x => new RepoViewListItem(x));

        var userRepos = repos.Where(x => x.OwningAccountName.Equals(_selectedAccount, StringComparison.OrdinalIgnoreCase));
        var userPublicRepos = userRepos.Where(x => !x.IsPrivate)
            .OrderBy(x => x.LastUpdated)
            .Select(x => new RepoViewListItem(x));

        var userPrivateRepos = userRepos.Where(x => x.IsPrivate)
            .OrderBy(x => x.LastUpdated)
            .Select(x => new RepoViewListItem(x));

        return userPrivateRepos
            .Concat(organizationRepos)
            .Concat(userPublicRepos);
    }

    /// <summary>
    /// Gets a value indicating whether the UI can skip the account page and switch to the repo page.
    /// </summary>
    /// <remarks>
    /// UI can skip the account tab and go to the repo page if the following conditions are met
    /// 1. DevHome has only 1 provider installed.
    /// 2. The provider has only 1 logged in account.
    /// </remarks>
    public bool CanSkipAccountConnection
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets or sets what page the user is currently on.  Used to branch logic depending on the page.
    /// </summary>
    internal PageKind CurrentPage
    {
        get; set;
    }

    public AddRepoViewModel(ISetupFlowStringResource stringResource, List<CloningInformation> previouslySelectedRepos)
    {
        _stringResource = stringResource;
        ChangeToUrlPage();

        // override changes ChangeToUrlPage to correctly set the state.
        UrlParsingError = string.Empty;
        ShouldShowUrlError = Visibility.Collapsed;
        ShouldPrimaryButtonBeEnabled = false;
        ShowErrorTextBox = Visibility.Collapsed;
        EverythingToClone = new ();

        _previouslySelectedRepos = previouslySelectedRepos ?? new List<CloningInformation>();
        EverythingToClone = new List<CloningInformation>(_previouslySelectedRepos);
    }

    /// <summary>
    /// Gets all the plugins the DevHome can see.
    /// </summary>
    /// <remarks>
    /// A valid plugin is one that has a repository provider and devid provider.
    /// </remarks>
    public void GetPlugins()
    {
        Log.Logger?.ReportInfo(Log.Component.RepoConfig, "Getting installed plugins with Repository and DevId providers");
        var pluginService = Application.Current.GetService<IPluginService>();
        var pluginWrappers = pluginService.GetInstalledPluginsAsync().Result;
        var plugins = pluginWrappers.Where(
            plugin => plugin.HasProviderType(ProviderType.Repository) &&
            plugin.HasProviderType(ProviderType.DeveloperId));

        _providers = new RepositoryProviders(plugins);

        // Start all plugins to get the DisplayName of each provider.
        _providers.StartAllPlugins();

        ProviderNames = new ObservableCollection<string>(_providers.GetAllProviderNames());
        TelemetryFactory.Get<ITelemetry>().Log("RepoTool_SearchForProviders_Event", LogLevel.Measure, new ProviderEvent(ProviderNames.Count));
    }

    public void ChangeToUrlPage()
    {
        Log.Logger?.ReportInfo(Log.Component.RepoConfig, "Changing to Url page");
        ShowUrlPage = Visibility.Visible;
        ShowAccountPage = Visibility.Collapsed;
        ShowRepoPage = Visibility.Collapsed;
        IsUrlAccountButtonChecked = true;
        IsAccountToggleButtonChecked = false;
        CurrentPage = PageKind.AddViaUrl;
        PrimaryButtonText = _stringResource.GetLocalized(StringResourceKey.RepoEverythingElsePrimaryButtonText);
    }

    public void ChangeToAccountPage()
    {
        Log.Logger?.ReportInfo(Log.Component.RepoConfig, "Changing to Account page");
        ShouldShowUrlError = Visibility.Collapsed;
        ShowUrlPage = Visibility.Collapsed;
        ShowAccountPage = Visibility.Visible;
        ShowRepoPage = Visibility.Collapsed;
        IsUrlAccountButtonChecked = false;
        IsAccountToggleButtonChecked = true;
        CurrentPage = PageKind.AddViaAccount;
        PrimaryButtonText = _stringResource.GetLocalized(StringResourceKey.RepoAccountPagePrimaryButtonText);

        if (ProviderNames.Count == 1)
        {
            _providers.StartIfNotRunning(ProviderNames[0]);
            var accounts = _providers.GetAllLoggedInAccounts(ProviderNames[0]);
            if (accounts.Count() == 1)
            {
                CanSkipAccountConnection = true;
            }
        }
    }

    public void ChangeToRepoPage()
    {
        Log.Logger?.ReportInfo(Log.Component.RepoConfig, "Changing to Repo page");
        ShowUrlPage = Visibility.Collapsed;
        ShowAccountPage = Visibility.Collapsed;
        ShowRepoPage = Visibility.Visible;
        CurrentPage = PageKind.Repositories;
        PrimaryButtonText = _stringResource.GetLocalized(StringResourceKey.RepoEverythingElsePrimaryButtonText);

        // The only way to get the repo page is through the account page.
        // No need to change toggle buttons.
    }

    /// <summary>
    /// Makes sure all needed information is present.
    /// </summary>
    /// <returns>True if all information is in order, otherwise false</returns>
    public bool ValidateRepoInformation()
    {
        if (CurrentPage == PageKind.AddViaUrl)
        {
            // Check if Url field is empty
            if (string.IsNullOrEmpty(Url))
            {
                return false;
            }

            if (!Uri.TryCreate(Url, UriKind.RelativeOrAbsolute, out _))
            {
                UrlParsingError = _stringResource.GetLocalized(StringResourceKey.UrlValidationBadUrl);
                ShouldShowUrlError = Visibility.Visible;
                return false;
            }

            ShouldShowUrlError = Visibility.Collapsed;
            return true;
        }
        else if (CurrentPage == PageKind.AddViaAccount || CurrentPage == PageKind.Repositories)
        {
             return EverythingToClone.Count > 0;
        }
        else
        {
            return false;
        }
    }

    public void EnablePrimaryButton()
    {
        ShouldPrimaryButtonBeEnabled = true;
    }

    public void DisablePrimaryButton()
    {
        ShouldPrimaryButtonBeEnabled = false;
    }

    /// <summary>
    /// Gets all the accounts for a provider and updates the UI.
    /// </summary>
    /// <param name="repositoryProviderName">The provider the user wants to use.</param>
    public async Task GetAccountsAsync(string repositoryProviderName)
    {
        await Task.Run(() => _providers.StartIfNotRunning(repositoryProviderName));
        var loggedInAccounts = await Task.Run(() => _providers.GetAllLoggedInAccounts(repositoryProviderName));
        if (!loggedInAccounts.Any())
        {
            TelemetryFactory.Get<ITelemetry>().Log("RepoTool_GetAccount_Event", LogLevel.Measure, new RepoDialogGetAccountEvent(repositoryProviderName, alreadyLoggedIn: false));

            // Throw away developer id becase we're calling GetAllLoggedInAccounts in anticipation
            // of 1 Provider : N DeveloperIds
            await Task.Run(() => _providers.LogInToProvider(repositoryProviderName));
            loggedInAccounts = await Task.Run(() => _providers.GetAllLoggedInAccounts(repositoryProviderName));
        }
        else
        {
            TelemetryFactory.Get<ITelemetry>().Log("RepoTool_GetAccount_Event", LogLevel.Measure, new RepoDialogGetAccountEvent(repositoryProviderName, alreadyLoggedIn: true));
        }

        Accounts = new ObservableCollection<string>(loggedInAccounts.Select(x => x.LoginId()));
    }

    /// <summary>
    /// Adds repositories to the list of repos to clone.
    /// Removes repositories from the list of repos to clone.
    /// </summary>
    /// <param name="providerName">The provider that is used to do the cloning.</param>
    /// <param name="accountName">The account used to authenticate into the provider.</param>
    /// <param name="repositoriesToAdd">Repositories to add</param>
    /// <param name="repositoriesToRemove">Repositories to remove.</param>
    /// <remarks>
    /// User has to go through the account screen to get here.  The login id to use is known.
    /// Repos will not be saved when filtering is taking place, or SelectRange is being called.
    /// Both filtering and SelectRange kicks off this event and EverythingToClone should not be altered at this time.
    /// </remarks>
    public void AddOrRemoveRepository(string providerName, string accountName, IList<object> repositoriesToAdd, IList<object> repositoriesToRemove)
    {
        // return right away if this event is fired because of filtering or SelectRange is called.
        if (_isFiltering || IsCallingSelectRange)
        {
            return;
        }

        Log.Logger?.ReportInfo(Log.Component.RepoConfig, $"Adding and removing repositories");
        var developerId = _providers.GetAllLoggedInAccounts(providerName).FirstOrDefault(x => x.LoginId() == accountName);
        foreach (RepoViewListItem repositoryToRemove in repositoriesToRemove)
        {
            Log.Logger?.ReportInfo(Log.Component.RepoConfig, $"Removing repository {repositoryToRemove}");

            var repoToRemove = _repositoriesForAccount.FirstOrDefault(x => x.DisplayName.Equals(repositoryToRemove.RepoName, StringComparison.OrdinalIgnoreCase));
            if (repoToRemove == null)
            {
                continue;
            }

            var cloningInformation = new CloningInformation(repoToRemove);
            cloningInformation.ProviderName = _providers.DisplayName(providerName);
            cloningInformation.OwningAccount = developerId;

            EverythingToClone.Remove(cloningInformation);
        }

        foreach (RepoViewListItem repositoryToAdd in repositoriesToAdd)
        {
            Log.Logger?.ReportInfo(Log.Component.RepoConfig, $"Adding repository {repositoryToAdd}");
            var repoToAdd = _repositoriesForAccount.FirstOrDefault(x => x.DisplayName.Equals(repositoryToAdd.RepoName, StringComparison.OrdinalIgnoreCase));
            if (repoToAdd == null)
            {
                continue;
            }

            var cloningInformation = new CloningInformation(repoToAdd);
            cloningInformation.ProviderName = _providers.DisplayName(providerName);
            cloningInformation.OwningAccount = developerId;
            cloningInformation.EditClonePathAutomationName = _stringResource.GetLocalized(StringResourceKey.RepoPageEditClonePathAutomationProperties, $"{providerName}/{repositoryToAdd}");
            cloningInformation.RemoveFromCloningAutomationName = _stringResource.GetLocalized(StringResourceKey.RepoPageRemoveRepoAutomationProperties, $"{providerName}/{repositoryToAdd}");

            EverythingToClone.Add(cloningInformation);
        }
    }

    /// <summary>
    /// Adds a repository from the URL page. Steps to determine what repoProvider to use.
    /// 1. All providers are asked "Can you parse this into a URL you understand."  If yes, that provider to clone the repo.
    /// 2. If no providers can parse the URL a fall back "GitProvider" is used that uses libgit2sharp to clone the repo.
    /// ShouldShowUrlError is set here.
    /// </summary>
    /// <remarks>
    /// If ShouldShowUrlError == Visible the repo is not added to the list of repos to clone.
    /// </remarks>
    /// <param name="cloneLocation">The location to clone the repo to</param>
    public void AddRepositoryViaUri(string url, string cloneLocation)
    {
        // If the url isn't valid don't bother finding a provider.
        Uri parsedUri;
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out parsedUri))
        {
            UrlParsingError = _stringResource.GetLocalized(StringResourceKey.UrlValidationBadUrl);
            ShouldShowUrlError = Visibility.Visible;
            return;
        }

        // If user entered a relative Uri put it into a UriBuilder to turn it into an
        // absolute Uri.  UriBuilder prepends the https scheme
        if (!parsedUri.IsAbsoluteUri)
        {
            var uriBuilder = new UriBuilder(parsedUri.OriginalString);
            uriBuilder.Port = -1;
            parsedUri = uriBuilder.Uri;
        }

        // If the URL points to a private repo the URL tab has no way of knowing what account has access.
        // Keep owning account null to make github extension try all logged in accounts.
        (string, IRepository) providerNameAndRepo;

        try
        {
            providerNameAndRepo = _providers.ParseRepositoryFromUri(parsedUri);
        }
        catch (Exception e)
        {
            // Catching should not be used for branching logic.
            // However, I forgot to consider the scenario where the URL can be parsed
            // but the repo can't be found.  This can happen if
            // 1. Any logged in account does not have access
            // 2. The repo does not exist.
            UrlParsingError = _stringResource.GetLocalized(StringResourceKey.UrlValidationNotFound);
            ShouldShowUrlError = Visibility.Visible;
            Log.Logger?.ReportInfo(Log.Component.RepoConfig, e.ToString());
            TelemetryFactory.Get<ITelemetry>().LogMeasure("RepoDialog_RepoNotFound_Event");
            return;
        }

        CloningInformation cloningInformation;
        if (providerNameAndRepo.Item2 != null)
        {
            // A provider parsed the URL and at least 1 logged in account has access to the repo.
            var repository = providerNameAndRepo.Item2;
            cloningInformation = new CloningInformation(repository);
            cloningInformation.ProviderName = providerNameAndRepo.Item1;
            cloningInformation.CloningLocation = new DirectoryInfo(cloneLocation);
        }
        else
        {
            Log.Logger?.ReportInfo(Log.Component.RepoConfig, "No providers could parse the Url.  Falling back to internal git provider");

            // No providers can parse the Url.
            // Fall back to a git Url.
            cloningInformation = new CloningInformation(new GenericRepository(parsedUri));
            cloningInformation.ProviderName = "git";
            cloningInformation.CloningLocation = new DirectoryInfo(cloneLocation);
        }

        // User could paste in a url of an already added repo.  Check for that here.
        if (_previouslySelectedRepos.Any(x => x.RepositoryToClone.OwningAccountName.Equals(cloningInformation.RepositoryToClone.OwningAccountName, StringComparison.OrdinalIgnoreCase)
            && x.RepositoryToClone.DisplayName.Equals(cloningInformation.RepositoryToClone.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            UrlParsingError = _stringResource.GetLocalized(StringResourceKey.UrlValidationRepoAlreadyAdded);
            ShouldShowUrlError = Visibility.Visible;
            Log.Logger?.ReportInfo(Log.Component.RepoConfig, "Repository has already been added.");
            TelemetryFactory.Get<ITelemetry>().LogMeasure("RepoTool_RepoAlreadyAdded_Event");
            return;
        }

        Log.Logger?.ReportInfo(Log.Component.RepoConfig, $"Adding repository to clone {cloningInformation.RepositoryId} to location '{cloneLocation}'");
        EverythingToClone.Add(cloningInformation);
    }

    /// <summary>
    /// Gets all the repositories for the the specified provider and account.
    /// </summary>
    /// <param name="repositoryProvider">The provider.  This should match IRepositoryProvider.LoginId</param>
    /// <param name="loginId">The login Id to get the repositories for</param>
    public IEnumerable<RepoViewListItem> GetRepositories(string repositoryProvider, string loginId)
    {
        _selectedAccount = loginId;

        TelemetryFactory.Get<ITelemetry>().Log("RepoTool_GetRepos_Event", LogLevel.Measure, new RepoToolEvent("GettingAllLoggedInAccounts"));
        var loggedInDeveloper = _providers.GetAllLoggedInAccounts(repositoryProvider).FirstOrDefault(x => x.LoginId() == loginId);

        TelemetryFactory.Get<ITelemetry>().Log("RepoTool_GetRepos_Event", LogLevel.Measure, new RepoToolEvent("GettingAllRepos"));
        _repositoriesForAccount = _providers.GetAllRepositories(repositoryProvider, loggedInDeveloper);

        Repositories = new ObservableCollection<RepoViewListItem>(OrderRepos(_repositoriesForAccount));

        return _previouslySelectedRepos.Where(x => x.OwningAccount != null)
            .Where(x => x.ProviderName.Equals(repositoryProvider, StringComparison.OrdinalIgnoreCase)
            && x.OwningAccount.LoginId().Equals(loginId, StringComparison.OrdinalIgnoreCase))
            .Select(x => new RepoViewListItem(x.RepositoryToClone));
    }

    /// <summary>
    /// Sets the clone location for all repositories to _cloneLocation
    /// </summary>
    /// <param name="cloneLocation">The location to clone all repositories to.</param>
    public void SetCloneLocation(string cloneLocation)
    {
        Log.Logger?.ReportInfo(Log.Component.RepoConfig, $"Setting the clone location for all repositories to {cloneLocation}");
        foreach (var cloningInformation in EverythingToClone)
        {
            cloningInformation.CloningLocation = new DirectoryInfo(cloneLocation);
        }
    }
}
