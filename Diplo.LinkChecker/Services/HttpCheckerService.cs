﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Caching;
using System.Text;
using System.Threading.Tasks;
using Diplo.LinkChecker.Models;
using Umbraco.Core.Logging;

namespace Diplo.LinkChecker.Services
{
    /// <summary>
    /// A service for checking the status of resources via HTTP eg. links
    /// </summary>
    public class HttpCheckerService
    {
        private static readonly HttpClient internalClient;

        static HttpCheckerService()
        {
            internalClient = new HttpClient(Config.GetClientHandler());
            internalClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            internalClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            internalClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Gets or sets the user agent string when making internal (local) requests to get the Umbraco page content
        /// </summary>
        public string InternalUserAgent { get; set; }

        /// <summary>
        /// Get or set the user agent string used when making external HTTP requests
        /// </summary>
        public string ExternalUserAgent { get; set; }

        /// <summary>
        /// Gets or sets the period to cache the status of checked links
        /// </summary>
        public TimeSpan CachePeriod { get; set; }

        /// <summary>
        /// Gets or sets the timout of HTTP requests (default is 30 seconds)
        /// </summary>
        public TimeSpan Timeout { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpCheckerService"/> class with the default settings
        /// </summary>
        public HttpCheckerService()
        {
            this.InternalUserAgent = "Mozilla/5.0 (Diplo LinkChecker 1.1 for Umbraco)";
            this.ExternalUserAgent = "Mozilla/5.0 (compatible; LinkChecker 1.0; MSIE 11.0; Windows NT 6.2; WOW64; Trident/6.0)";
            this.CachePeriod = TimeSpan.FromMinutes(1);
            this.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Gets the HTML returned form a request to web page at a specific URL
        /// </summary>
        /// <remarks>
        /// Runs asynchronously
        /// </remarks>
        /// <param name="url">The URL to check</param>
        /// <returns>A string containing the HTML that has been retrieved</returns>
        /// <exception cref="System.ArgumentNullException">The URL to fetch HTML from cannot be null</exception>
        public async Task<string> GetHtmlFromUrl(Uri url)
        {
            if (url == null)
            {
                throw new ArgumentNullException("url", "The URL to fetch HTML from cannot be null");
            }

            try
            {
                using (var message = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    message.Headers.Add("user-agent", this.InternalUserAgent);

                    using (var response = await internalClient.SendAsync(message))
                    {
                        response.EnsureSuccessStatusCode();

                        if (response.Content.Headers.ContentType != null && response.Content.Headers.ContentType.MediaType == "text/html")
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                string content = string.Empty;

                                if (!string.IsNullOrWhiteSpace(response.Content.Headers.ContentType.CharSet))
                                {
                                    var buffer = await response.Content.ReadAsByteArrayAsync();
                                    var encoding = Encoding.GetEncoding(response.Content.Headers.ContentType.CharSet);
                                    return WebUtility.HtmlDecode(encoding.GetString(buffer));
                                }
                                else
                                {
                                    content = await response.Content.ReadAsStringAsync();
                                    return WebUtility.HtmlDecode(content);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error<HttpCheckerService>("Diplo LinkChecker could not check URL: " + url, ex);
            }

            return String.Empty;
        }

        /// <summary>
        /// Checks a list of supplied links asynchronously
        /// </summary>
        /// <param name="itemsToCheck">The list of links to check</param>
        /// <returns>The links but with updated status</returns>
        public async Task<IEnumerable<Link>> CheckLinks(IEnumerable<Link> itemsToCheck)
        {
            if (itemsToCheck == null)
            {
                throw new ArgumentNullException("The list of links to check cannot be null");
            }

            using (HttpClient client = new HttpClient(Config.GetClientHandler()))
            {
                client.DefaultRequestHeaders.Add("user-agent", this.ExternalUserAgent);
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
                client.Timeout = this.Timeout;

                IEnumerable<Task<Link>> checkUrlsQuery =
                    from item in itemsToCheck select CheckUrl(client, item);

                Task<Link>[] itemTasks = checkUrlsQuery.ToArray();

                var results = await Task.WhenAll(itemTasks);

                return results;
            }
        }

        /// <summary>
        /// Checks the URL of a given link using the supplied HTTP client
        /// </summary>
        /// <param name="client">The client to check the link with</param>
        /// <param name="link">The link to check</param>
        /// <remarks>
        /// Once a link has been checked it's status is cached in memory and it won't be checked again for a set period
        /// </remarks>
        /// <returns>The checked link with it's status updated</returns>
        private async Task<Link> CheckUrl(HttpClient client, Link link)
        {
            var existing = MemoryCache.Default.Get(link.Url.AbsoluteUri) as Link;

            if (existing != null)
            {
                existing.CheckedPreviously = true;
                return existing;
            }

            try
            {
                using (var message = new HttpRequestMessage(HttpMethod.Head, link.Url))
                {
                    using (var response = await client.SendAsync(message))
                    {
                        link.Status = response.ReasonPhrase;
                        link.StatusCode = ((int)response.StatusCode).ToString();
                        link.IsSuccessCode = response.IsSuccessStatusCode;
                        link.ContentType = response.Content.Headers.ContentType != null ? response.Content.Headers.ContentType.MediaType : String.Empty;

                        MemoryCache.Default.Add(link.Url.AbsoluteUri, link, DateTime.Now.Add(this.CachePeriod));
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                link.Status = ex.Message;

                if (ex.InnerException != null && ex.InnerException is WebException)
                {
                    if (ex.InnerException is WebException webEx)
                    {
                        link.Error = webEx.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                link.Status = "ERROR";
                link.Error = ex.Message;
            }

            return link;
        }
    }
}