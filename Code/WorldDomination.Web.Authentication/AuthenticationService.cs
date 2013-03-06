﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.ComponentModel.Composition.ReflectionModel;
using System.Configuration;
using System.Linq;
using WorldDomination.Web.Authentication.Config;
using WorldDomination.Web.Authentication.Facebook;
using WorldDomination.Web.Authentication.Google;
using WorldDomination.Web.Authentication.Twitter;

namespace WorldDomination.Web.Authentication
{
    public class AuthenticationService : IAuthenticationService
    {
        private readonly Lazy<IEnumerable<Type>> _discoveredProviders = new Lazy<IEnumerable<Type>>(GetExportedTypes<IAuthenticationProvider>);
        public IDictionary<string, IAuthenticationProvider> AuthenticationProviders { get; private set; }

        public AuthenticationService()
        {
            var providerConfig = ConfigurationManager.GetSection("authenticationProviders") as ProviderConfiguration;

            if (providerConfig != null)
            {
                Initialize(providerConfig);
            }
        }

        public AuthenticationService(IEnumerable<IAuthenticationProvider> providers)
        {
            // Skip the config-based initialization, we got a list of providers directly
            foreach (var provider in providers)
            {
                AddProvider(provider);
            }
        }

        public AuthenticationService(ProviderConfiguration providerConfiguration,
                                     IList<string> scope = null, IRestClientFactory restClientFactory = null)
        {
            Initialize(providerConfiguration, scope, restClientFactory);
        }

        public void Initialize(ProviderConfiguration providerConfiguration, IList<string> scope = null, IRestClientFactory restClientFactory = null)
        {
            if (providerConfiguration == null)
            {
                throw new ArgumentNullException("providerConfiguration");
            }

            if (providerConfiguration.Providers == null)
            {
                throw new ArgumentException("providerConfiguration.Providers");
            }

            foreach (ProviderKey provider in providerConfiguration.Providers)
            {
                IAuthenticationProvider authenticationProvider;

                switch (provider.Name.ToLowerInvariant())
                {
                    case "facebook":
                        authenticationProvider = new FacebookProvider(provider, scope, restClientFactory);
                        break;
                    case "google":
                        authenticationProvider = new GoogleProvider(provider, scope, restClientFactory);
                        break;
                    case "twitter":
                        authenticationProvider = new TwitterProvider(provider, restClientFactory);
                        break;
                    default:
                        authenticationProvider = DiscoverProvider(provider, restClientFactory);
                        break;
                }

                AddProvider(authenticationProvider);
            }
        }

        private static IEnumerable<Type> GetExportedTypes<T>()
        {
            var catalog = new AggregateCatalog(
                new DirectoryCatalog(@".", "*"),
                new DirectoryCatalog(@".\bin", "*")
                );

            return catalog.Parts
                .Select(part => ComposablePartExportType<T>(part))
                .Where(t => t != null)
                .ToList();
        }

        private static Type ComposablePartExportType<T>(ComposablePartDefinition part)
        {
            if (part.ExportDefinitions.Any(
                def => def.Metadata.ContainsKey("ExportTypeIdentity") &&
                    def.Metadata["ExportTypeIdentity"].Equals(typeof(T).FullName)))
            {
                return ReflectionModelServices.GetPartType(part).Value;
            }

            return null;
        }

        private IAuthenticationProvider DiscoverProvider(ProviderKey providerKey, IRestClientFactory restClientFactory)
        {
            var name = providerKey.Name.ToLowerInvariant();

            var provider = _discoveredProviders.Value.SingleOrDefault(x => x.Name.ToLowerInvariant().StartsWith(name));

            if (provider == null)
            {
                throw new ApplicationException(string.Format("Unable to find provider {0}, ensure you registered in the web.config or via code.", name));
            }

            var parameters = new CustomProviderParams
            {
                Key = providerKey.Key,
                Secret = providerKey.Secret,
                RestClientFactory = restClientFactory
            };

            return Activator.CreateInstance(provider, parameters) as IAuthenticationProvider;
        }

        private IAuthenticationProvider GetAuthenticationProvider(string providerKey)
        {
            IAuthenticationProvider authenticationProvider = null;

            if (AuthenticationProviders != null)
            {
                AuthenticationProviders.TryGetValue(providerKey.ToLowerInvariant(), out authenticationProvider);
            }

            if (authenticationProvider == null)
            {
                throw new AuthenticationException(string.Format("No '{0}' provider has been added.", providerKey));
            }

            return authenticationProvider;
        }

        private static Uri CreateCallBackUri(string providerKey, Uri requestUrl, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }

            if (string.IsNullOrEmpty(providerKey))
            {
                throw new ArgumentNullException("providerKey");
            }

            if (requestUrl == null)
            {
                throw new ArgumentNullException("requestUrl");
            }

            var builder = new UriBuilder(requestUrl)
            {
                Path = path,
                Query = "providerkey=" + providerKey.ToLowerInvariant()
            };

            // Don't include port 80/443 in the Uri.
            if (builder.Uri.IsDefaultPort)
            {
                builder.Port = -1;
            }

            return builder.Uri;
        }

        #region Implementation of IAuthenticationService

        public void AddProvider(IAuthenticationProvider authenticationProvider)
        {
            if (AuthenticationProviders == null)
            {
                AuthenticationProviders = new Dictionary<string, IAuthenticationProvider>();
            }

            var providerName = authenticationProvider.Name.ToLowerInvariant();

            // Does this provider already exist?
            if (AuthenticationProviders.ContainsKey(providerName))
            {
                throw new AuthenticationException(string.Format("Trying to add a {0} provider, but one already exists.", providerName));
            }

            AuthenticationProviders.Add(providerName, authenticationProvider);
        }

        public Uri RedirectToAuthenticationProvider(string providerKey, Uri callBackUri = null)
        {
            if (string.IsNullOrEmpty(providerKey))
            {
                throw new ArgumentNullException("providerKey");
            }

            // Determine the provider.
            var authenticationProvider = GetAuthenticationProvider(providerKey);
            if (authenticationProvider == null)
            {
                throw new InvalidOperationException("No provider was found for the key: " + providerKey);
            }

            // Retrieve the default settings for this provider.
            var authenticationServiceSettings = authenticationProvider.DefaultAuthenticationServiceSettings;

            // Have we provided an specific callBack uri?
            if (callBackUri != null)
            {
                authenticationServiceSettings.CallBackUri = callBackUri;
            }

            return authenticationProvider.RedirectToAuthenticate(authenticationServiceSettings);
        }

        public Uri RedirectToAuthenticationProvider(IAuthenticationServiceSettings authenticationServiceSettings)
        {
            if (authenticationServiceSettings == null)
            {
                throw new ArgumentNullException("authenticationServiceSettings");
            }

            if (string.IsNullOrEmpty(authenticationServiceSettings.ProviderName))
            {
                throw new ArgumentException("authenticationServiceSettings.providerName");
            }

            if (authenticationServiceSettings.CallBackUri == null ||
                string.IsNullOrEmpty(authenticationServiceSettings.CallBackUri.AbsoluteUri))
            {
                throw new ArgumentException("authenticationServiceSettings.CallBackUri");
            }

            var authenticationProvider = GetAuthenticationProvider(authenticationServiceSettings.ProviderName);
            if (authenticationProvider == null)
            {
                throw new InvalidOperationException("No Provider found for the Provider Name: " + authenticationServiceSettings.ProviderName);
            }

             return authenticationProvider.RedirectToAuthenticate(authenticationServiceSettings);
        }

        public IAuthenticatedClient GetAuthenticatedClient(IAuthenticationServiceSettings authenticationServiceSettings,
                                                           dynamic requestParameters)
        {
            var querystringParameters = new NameValueCollection();

            foreach (var item in requestParameters)
            {
                querystringParameters.Add(item, requestParameters[item]);
            }

            return GetAuthenticatedClient(authenticationServiceSettings, querystringParameters);
        }

        public IAuthenticatedClient GetAuthenticatedClient(IAuthenticationServiceSettings authenticationServiceSettings,
                                                           NameValueCollection queryStringParameters)
        {
            if (authenticationServiceSettings == null)
            {
                throw new ArgumentNullException("authenticationServiceSettings");
            }

            if (queryStringParameters == null)
            {
                throw new ArgumentNullException("queryStringParameters");
            }

            if (queryStringParameters.Count <= 0)
            {
                throw new ArgumentOutOfRangeException("queryStringParameters");
            }

            // Grab the Authentication Client.
            var authenticationProvider = GetAuthenticationProvider(authenticationServiceSettings.ProviderName);

            return authenticationProvider.AuthenticateClient(authenticationServiceSettings, queryStringParameters);
        }

        public IAuthenticationServiceSettings GetAuthenticateServiceSettings(string providerKey, Uri requestUrl,
            string path = "/authentication/authenticatecallback")
        {
            var name = providerKey.ToLowerInvariant();

            if (string.IsNullOrEmpty(providerKey))
            {
                throw new ArgumentNullException("providerKey");
            }

            IAuthenticationServiceSettings settings;
            switch (name)
            {
                case "facebook":
                    settings = new FacebookAuthenticationServiceSettings();
                    break;
                case "google":
                    settings = new GoogleAuthenticationServiceSettings();
                    break;
                case "twitter":
                    settings = new TwitterAuthenticationServiceSettings();
                    break;
                default:
                    settings = AuthenticationProviders[name].DefaultAuthenticationServiceSettings;
                    break;
            }

            // Setup up some defaults.
            settings.State = Guid.NewGuid().ToString();
            settings.CallBackUri = CreateCallBackUri(providerKey, requestUrl, path);

            return settings;
        }

        #endregion
    }
}