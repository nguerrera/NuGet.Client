﻿using Newtonsoft.Json.Linq;
using NuGet.Data;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JsonLD.Core;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Globalization;


namespace NuGet.Client.V3
{
    /// <summary>
    /// Class used by all the APi v3 clients (like VisualStudio UI, Powershell and commandline to talk to Api v3 endpoints).
    /// *TODOS:
    /// Integrate ServiceDiscovery.
    /// Setting user agent
    /// Writing traces disabled.
    /// No "SearchFilter". Instead pass list of Framework names and prerelease.
    /// Recording metric is disabled as of now.
    /// Update the csproj to match with the rest of the solution.
    /// </summary>
    public class NuGetV3Client : IDisposable
    {
        private DataClient _client;     
        private Uri _root;
        private string _userAgent;
        private System.Net.Http.HttpClient _http;

        private static readonly Uri[] ResultItemRequiredProperties = new Uri[] {
            new Uri("http://schema.nuget.org/schema#registration")
        };

        private static readonly Uri[] PackageRequiredProperties = new Uri[] {
            new Uri("http://schema.nuget.org/schema#catalogEntry")
        };

        private static readonly Uri[] CatalogRequiredProperties = new Uri[] {
            new Uri("http://schema.nuget.org/schema#items")
        };

        private static readonly Uri[] PackageDetailsRequiredProperties = new Uri[] {
            new Uri("http://schema.nuget.org/schema#authors"),
            new Uri("http://schema.nuget.org/schema#description"),
            new Uri("http://schema.nuget.org/schema#iconUrl"),
            new Uri("http://schema.nuget.org/schema#id"),
            new Uri("http://schema.nuget.org/schema#language"),
            new Uri("http://schema.nuget.org/schema#licenseUrl"),
            new Uri("http://schema.nuget.org/schema#minClientVersion"),
            new Uri("http://schema.nuget.org/schema#projectUrl"),
            new Uri("http://schema.nuget.org/schema#published"),
            new Uri("http://schema.nuget.org/schema#requireLicenseAcceptance"),
            new Uri("http://schema.nuget.org/schema#summary"),
            new Uri("http://schema.nuget.org/schema#tags"),
            new Uri("http://schema.nuget.org/schema#title"),
            new Uri("http://schema.nuget.org/schema#version"),
        };

        private static readonly DataCacheOptions DefaultCacheOptions = new DataCacheOptions()
        {
            UseFileCache = true,
            MaxCacheLife = TimeSpan.FromHours(2)
        };

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "The HttpClient can be left open until VS shuts down.")]
        public NuGetV3Client(string rootUrl, string host)
        {            
            _root = new Uri(rootUrl);

            //TODO: Get context from current UI activity (PowerShell, Dialog, etc.)
            _userAgent = String.Format("NuGetv3Client", host);

            var handler = new HttpClientHandler();
            _http = new System.Net.Http.HttpClient(handler);

            // Check if we should disable the browser file cache
            FileCacheBase cache = new BrowserFileCache();
            if (String.Equals(Environment.GetEnvironmentVariable("NUGET_DISABLE_IE_CACHE"), "true", StringComparison.OrdinalIgnoreCase))
            {
                cache = new NullFileCache();
            }

            cache = new NullFileCache(); 

            _client = new DataClient(
                handler,
                cache);
        }

        public async Task<IEnumerable<JObject>> Search(string searchTerm, IEnumerable<string> supportedFrameworkNames,bool includePrerelease, int skip, int take, System.Threading.CancellationToken cancellationToken)
        {
            //*TODOS: Get the search service URL from the service. Once it is integrated with ServiceDiscovery GetServiceUri would go away.
            cancellationToken.ThrowIfCancellationRequested();
            var searchService = await GetServiceUri(ServiceUris.SearchQueryService);
            if (String.IsNullOrEmpty(searchService))
            {
                throw new NuGetProtocolException(Strings.Protocol_MissingSearchService);
            }
            cancellationToken.ThrowIfCancellationRequested();

            // Construct the query
            var queryUrl = new UriBuilder(searchService);
            string queryString =
                "q=" + searchTerm +
                "&skip=" + skip.ToString() +
                "&take=" + take.ToString() +
                "&includePrerelease=" + includePrerelease.ToString().ToLowerInvariant();
            string frameworks =
                String.Join("&",
                    supportedFrameworkNames.Select(
                        fx => "supportedFramework=" + fx));

            if (!String.IsNullOrEmpty(frameworks))
            {
                queryString += "&" + frameworks;
            }
            queryUrl.Query = queryString;

           
            var queryUri = queryUrl.Uri;
            var results = await _client.GetJObjectAsync(queryUri, DefaultCacheOptions);
            cancellationToken.ThrowIfCancellationRequested();
            if (results == null)
            {              
                return Enumerable.Empty<JObject>();
            }
            var data = results.Value<JArray>("data");
            if (data == null)
            {               
                return Enumerable.Empty<JObject>();
            }

            // Resolve all the objects
            List<JObject> outputs = new List<JObject>(take);
            foreach (var result in data.Take(take).Cast<JObject>())
            {
                var output = await ProcessSearchResult(cancellationToken, result);
                if (output != null)
                {
                    outputs.Add(output);
                }
            }

            return outputs;
        }      
        
      
        public async Task<JObject> GetPackageMetadata(string id, NuGetVersion version)
        {
            return (await GetPackageMetadataById(id))
                .FirstOrDefault(p => String.Equals(p["version"].ToString(), version.ToNormalizedString(), StringComparison.OrdinalIgnoreCase));
        }

        public async Task<IEnumerable<JObject>> GetPackageMetadataById(string packageId)
        {
            // Get the base URL
            var baseUrl = await GetServiceUri(ServiceUris.RegistrationsBaseUrl);
            if (String.IsNullOrEmpty(baseUrl))
            {
                throw new NuGetProtocolException(Strings.Protocol_MissingRegistrationBase);
            }

            // Construct the URL
            var packageUrl = baseUrl.TrimEnd('/') + "/" + packageId.ToLowerInvariant() + "/index.json";

            // Resolve the catalog root
            // TODO: Validate these properties exist
            //var catalogPackage = await _client.Ensure(new Uri(packageUrl), CatalogRequiredProperties);

            var catalogPackage = await _client.GetJObjectAsync(new Uri(packageUrl), DefaultCacheOptions);

            if (catalogPackage["HttpStatusCode"] != null)
            {
                // Got an error response from the data client, so just return an empty array
                return Enumerable.Empty<JObject>();
            }
            // Descend through the items to find all the versions
            var versions = await Descend((JArray)catalogPackage["items"]);

            // Return the catalogEntry values
            return versions.Select(o =>
            {
                var result = (JObject)o["catalogEntry"];
                result[Properties.PackageContent] = o[Properties.PackageContent];
                return result;
            });
        }

        #region PrivateHelpers

        private async Task<IEnumerable<JObject>> Descend(JArray json)
        {
            List<IEnumerable<JObject>> lists = new List<IEnumerable<JObject>>();
            List<JObject> items = new List<JObject>();
            lists.Add(items);
            foreach (var item in json)
            {
                string type = item["@type"].ToString();
                if (Equals(type, "catalog:CatalogPage"))
                {
                    // TODO: fix this
                    throw new NotImplementedException();
                    //var resolved = await _client.Ensure(item, new[] {
                    //    new Uri("http://schema.nuget.org/schema#items")
                    //});
                    //Debug.Assert(resolved != null, "DataClient returned null from Ensure :(");
                    //lists.Add(await Descend((JArray)resolved["items"]));
                }
                else if (Equals(type, "Package"))
                {
                    // Yield this item with catalogEntry and it's subfields ensured
                    //var resolved = await _client.Ensure(item, PackageRequiredProperties);
                    //resolved["catalogEntry"] = await _client.Ensure(resolved["catalogEntry"], PackageDetailsRequiredProperties);
                    //items.Add((JObject)resolved);
                    throw new NotImplementedException();
                }
            }

            // Flatten the list and return it
            return lists.SelectMany(j => j);
        }

        private async Task<string> GetServiceUri(Uri type)
        {
            // Read the root document (usually out of the cache :))
            DataCacheOptions cacheOptions = new DataCacheOptions()
            {
                UseFileCache = true,
                MaxCacheLife = TimeSpan.FromDays(2)
            };

            var doc = await _client.GetJObjectAsync(_root, cacheOptions);
            var obj = JsonLdProcessor.Expand(doc).FirstOrDefault();
            if (obj == null)
            {
                throw new NuGetProtocolException(Strings.Protocol_IndexMissingResourcesNode);
            }
            var resources = obj[ServiceUris.Resources.ToString()] as JArray;
            if (resources == null)
            {
                throw new NuGetProtocolException(Strings.Protocol_IndexMissingResourcesNode);
            }

            // Query it for the requested service
            var candidates = (from resource in resources.OfType<JObject>()
                              let resourceType = resource["@type"].Select(t => t.ToString()).FirstOrDefault()
                              where resourceType != null && Equals(resourceType, type.ToString())
                              select resource)
                             .ToList();
       

            var selected = candidates.FirstOrDefault();

            if (selected != null)
            {            
                return selected.Value<string>("@id");
            }
            else
            {             
                return null;
            }
        }

        private async Task<JObject> ProcessSearchResult(System.Threading.CancellationToken cancellationToken, JObject result)
        {
            // Get the registration
            // TODO: check that all required items are coming back
            // result = (JObject)(await _client.Ensure(result, ResultItemRequiredProperties));

            var searchResult = new JObject();
            searchResult["id"] = result["id"];
            searchResult[Properties.LatestVersion] = result[Properties.Version];
            searchResult[Properties.Versions] = result[Properties.Versions];
            searchResult[Properties.Summary] = result[Properties.Summary];
            searchResult[Properties.Description] = result[Properties.Description];
            searchResult[Properties.IconUrl] = result[Properties.IconUrl];
            return searchResult;
        }

        #endregion PrivateHelpers

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _http.Dispose();
                    _client.Dispose();
                }
                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion


    }
}
