﻿using Birko.SuperFaktura.Response;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Birko.SuperFaktura
{
    public abstract class AbstractSuperFaktura
    {
        public const string APIAUTHKEYWORD = "SFAPI";
        public string APIURL { get; protected set; }

        public string Email { get; private set; }
        public string ApiKey { get; private set; }
        public int? CompanyId { get; private set; }
        public string AppTitle { get; private set; }
        public string Module { get; private set; }

        public bool EnsureSuccessStatusCode { get; set; } = true;
        public int TimeoutSeconds { get; set; } = 30;

        public RateLimits RateLimit { get; set; } = null;

        private static Dictionary<string, DateTime> _lastRequest = null;
        private static Dictionary<string, HttpClient> _clientList = null;
        private static Dictionary<string, int> _requestCount = null;

        private string ProfileKey
        {
            get
            {
                return string.Format("{0}-{1}", APIURL, ApiKey);
            }
        }

        public AbstractSuperFaktura(string email, string apiKey, string apptitle = null, string module = "API", int? companyId = null)
        {
            Email = email;
            ApiKey = apiKey;
            CompanyId = companyId;
            AppTitle = apptitle;
            Module = module;
        }

        protected HttpClient CreateClient(bool force = false)
        {
            if (_clientList == null)
            {
                _clientList = new Dictionary<string, HttpClient>();
            }
            HttpClient client = null;
            var key = ProfileKey;
            if (force || !_clientList.ContainsKey(key) || _clientList[key].Timeout.Seconds != TimeoutSeconds)
            {
                client = new HttpClient
                {
                    BaseAddress = new Uri(APIURL),
                    Timeout = new TimeSpan(0, 0, 0, TimeoutSeconds, 0)
                };
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", string.Format("{0} email={1}&apikey={2}&company_id={3}", APIAUTHKEYWORD, Email, ApiKey, CompanyId));
                if (!_clientList.ContainsKey(key))
                {
                    _clientList.Add(key, client);
                }
                else
                {
                    _clientList[key] = client;
                }
            }
            else
            {
                client = _clientList[key];
            }

            return client;
        }

        internal T DeserializeResult<T>(string result, JsonSerializerSettings setting = null)
        {
            TestError(result);
            result = TestThrottle(result);
            try
            {
                return JsonConvert.DeserializeObject<T>(result, setting);
            }
            catch (Exception ex)
            {
                var exception = new Exceptions.ParseException(ex.Message, string.Format("Returned Response: {0}", result));
                throw (exception);
            }
        }

        internal static void TestError(string result)
        {
            try
            {
                var testResult = JsonConvert.DeserializeObject<Response<ExpandoObject>>(result);
                if (testResult.Error != null && testResult.Error.Value > 0)
                {
                    var exception = new Exceptions.Exception(testResult.Error.Value, testResult.Message, testResult.ErrorMessage);
                    throw (exception);
                }
            }
            catch (Exception ex)
            {
                var exception = new Exceptions.ParseException(ex.Message, string.Format("Returned Response: {0}", result));
                throw (exception);
            }
        }

        [Obsolete("Throttled was moved to HTTP header by SF on 4.9.2018")]
        internal string TestThrottle(string result)
        {
            try
            {
                var testResult = (Newtonsoft.Json.Linq.JObject)JsonConvert.DeserializeObject(result);
                if (testResult.ContainsKey("throttled"))
                {
                    var text = testResult.Value<string>("throttled");
                    var startIndex = text.IndexOf("You have already made ");
                    var endIndex = text.IndexOf(" requests today.");
                    var stringCount = text.Substring(startIndex, endIndex - startIndex).Replace("You have already made ", string.Empty);
                    IncreaseRequestCount(ProfileKey, int.Parse(stringCount), true);
                    testResult.Remove("throttled");
                    return testResult.ToString();
                }
                return result;
            }
            catch (Exception ex)
            {
                var exception = new Exceptions.ParseException(ex.Message, string.Format("Returned Response: {0}", result));
                throw (exception);
            }
        }

        private void RequestDelay()
        {
            DateTime now = DateTime.Now;
            if (_lastRequest == null)
            {
                _lastRequest = new Dictionary<string, DateTime>();
            }
            if (_requestCount == null)
            {
                _requestCount = new Dictionary<string, int>();
            }
            var key = ProfileKey;
            //request throttling
            if (_requestCount.ContainsKey(key) && _requestCount[key] >= 1000)
            {
                Task.Delay(TimeSpan.FromSeconds(5)).Wait();
            }
            else if (_lastRequest.ContainsKey(key) && (now - _lastRequest[key]).Seconds <= 1)
            {
                Task.Delay(TimeSpan.FromSeconds(1)).Wait();
            }
            now = DateTime.Now;
            if (_lastRequest.ContainsKey(key))
            {
                _lastRequest[key] = now;
            }
            else
            {
                _lastRequest.Add(key, now);
            }
        }

        private void ParseResponse(HttpResponseMessage response)
        {
            if (response != null)
            {
                if (response.Headers != null && response.Headers.Any())
                {
                    if (
                        response.Headers.Contains("X-RateLimit-DailyLimit")
                        || response.Headers.Contains("X-RateLimit-DailyRemaining")
                        || response.Headers.Contains("X-RateLimit-DailyReset")
                        || response.Headers.Contains("X-RateLimit-MonthlyLimit")
                        || response.Headers.Contains("X-RateLimit-MonthlyRemaining")
                        || response.Headers.Contains("X-RateLimit-MonthlyReset")
                        || response.Headers.Contains("X-RateLimit-Message")
                    )
                    {
                        if (RateLimit == null)
                        {
                            RateLimit = new RateLimits();
                        }
                        RateLimit.Message = response.Headers.Contains("X-RateLimit-Message") ? response.Headers.GetValues("X-RateLimit-Message").Last() : null;
                        if (
                            response.Headers.Contains("X-RateLimit-DailyLimit")
                            && long.TryParse(response.Headers.GetValues("X-RateLimit-DailyLimit").Last(), out long limit)
                        )
                        {
                            if (RateLimit.Daily == null)
                            {
                                RateLimit.Daily = new DetailLimit();
                            }
                            RateLimit.Daily.Limit = limit;
                        }
                        if (
                            response.Headers.Contains("X-RateLimit-DailyRemaining")
                            && long.TryParse(response.Headers.GetValues("X-RateLimit-DailyRemaining").Last(), out limit)
                        )
                        {
                            if (RateLimit.Daily == null)
                            {
                                RateLimit.Daily = new DetailLimit();
                            }
                            RateLimit.Daily.Remaining = limit;
                        }
                        if (
                            response.Headers.Contains("X-RateLimit-DailyReset")
                            && DateTime.TryParseExact(response.Headers.GetValues("X-RateLimit-DailyReset").Last(), "dd.MM.yyyy hh:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
                        )
                        {
                            if (RateLimit.Daily == null)
                            {
                                RateLimit.Daily = new DetailLimit();
                            }
                            RateLimit.Daily.Reset = date;
                        }
                        if (
                            response.Headers.Contains("X-RateLimit-MonthlyLimit")
                            && long.TryParse(response.Headers.GetValues("X-RateLimit-MonthlyLimit").Last(), out limit)
                        )
                        {
                            if (RateLimit.Monthly == null)
                            {
                                RateLimit.Monthly = new DetailLimit();
                            }
                            RateLimit.Monthly.Limit = limit;
                        }
                        if (
                            response.Headers.Contains("X-RateLimit-MonthlyRemaining")
                            && long.TryParse(response.Headers.GetValues("X-RateLimit-MonthlyRemaining").Last(), out limit)
                        )
                        {
                            if (RateLimit.Monthly == null)
                            {
                                RateLimit.Monthly = new DetailLimit();
                            }
                            RateLimit.Monthly.Remaining = limit;
                        }
                        if (
                            response.Headers.Contains("X-RateLimit-MonthlyReset")
                            && DateTime.TryParseExact(response.Headers.GetValues("X-RateLimit-MonthlyReset").Last(), "dd.MM.yyyy hh:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                        {
                            if (RateLimit.Monthly == null)
                            {
                                RateLimit.Monthly = new DetailLimit();
                            }
                            RateLimit.Monthly.Reset = date;
                        }
                    }
                }
                if (EnsureSuccessStatusCode)
                {
                    response.EnsureSuccessStatusCode();
                }
            }
        }

        internal async Task<string> Get(string uri)
        {
            RequestDelay();
            var client = CreateClient();
            HttpResponseMessage response = null;
            try
            {
                IncreaseRequestCount(ProfileKey);
                response = await client.GetAsync(uri).ConfigureAwait(false);
                ParseResponse(response);
                if (response.IsSuccessStatusCode || !EnsureSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                return await Task.FromResult<string>(null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw HandleRequestException(uri, response, ex);
            }
        }

        internal async Task<byte[]> GetByte(string uri)
        {
            RequestDelay();
            var client = CreateClient();
            HttpResponseMessage response = null;
            try
            {
                IncreaseRequestCount(ProfileKey);
                response = await client.GetAsync(uri).ConfigureAwait(false);
                ParseResponse(response);
                if (response.IsSuccessStatusCode || !EnsureSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
                return await Task.FromResult<byte[]>(null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw HandleRequestException(uri, response, ex);
            }
        }

        internal async Task<string> Post(string uri, string data)
        {
            RequestDelay();
            var client = CreateClient();
            HttpResponseMessage response = null;
            try
            {
                IncreaseRequestCount(ProfileKey);
                response = await client.PostAsync(uri, new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", data) })).ConfigureAwait(false);
                ParseResponse(response);
                if (response.IsSuccessStatusCode || !EnsureSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                return await Task.FromResult<string>(null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw HandleRequestException(uri, response, ex, data);
            }
        }

        internal async Task<string> Post(string uri, object data)
        {
            return await Post(uri, JsonConvert.SerializeObject(data)).ConfigureAwait(false);
        }

        private static void IncreaseRequestCount(string key, int count = 1, bool set = false)
        {
            if (_requestCount == null)
            {
                _requestCount = new Dictionary<string, int>();
            }
            if (!_requestCount.ContainsKey(key))
            {
                _requestCount.Add(key, 0);
            }
            if (set)
            {
                _requestCount[key] = count;
            }
            else
            {
                _requestCount[key] += count;
            }
        }

        private Exceptions.Exception HandleRequestException(string uri, HttpResponseMessage response, Exception ex, string data = null)
        {
            //CreateClient(true);
            string content = null;
            if (response != null)
            {
                var contentTask = response.Content.ReadAsStringAsync();
                contentTask.ConfigureAwait(false);
                contentTask.Wait();
                content = contentTask.Result;
            }
            string message = string.Format("ReasonPhrase: {0}\nContent: {1}\nRequest: {2}\nData: {3}",
                    response,
                    content,
                    response?.RequestMessage,
                    data
                );
            return new Exceptions.Exception(null, message, ex, uri);
        }
    }

    public class SuperFaktura : AbstractSuperFaktura
    {
        private Clients _clients = null;
        private Expenses _expenses = null;
        private Invoices _invoices = null;
        private Stock _stock = null;

        public Clients Clients {
            get {
                if (_clients == null) {
                    _clients = new Clients(this);
                }
                return _clients;
            }
        }

        public Expenses Expenses {
            get
            {
                if (_expenses == null)
                {
                    _expenses = new Expenses(this);
                }
                return _expenses;
            }
        }
        public Invoices Invoices {
            get
            {
                if (_invoices == null)
                {
                    _invoices = new Invoices(this);
                }
                return _invoices;
            }
        }

        public Stock Stock {
            get
            {
                if (_stock== null)
                {
                    _stock = new Stock(this);
                }
                return _stock;
            }
        }

        public SuperFaktura(string email, string apiKey, string apptitle = null, string module = "API", int? companyId = null)
            : base(email,apiKey, apptitle, module, companyId)
        {
            APIURL = "https://moja.superfaktura.sk/";
        }

        public async Task<Dictionary<int, string>> GetCountries()
        {
            var result = await Get("countries").ConfigureAwait(false);
            try
            {
                return DeserializeResult<Dictionary<int, string>>(result);
            }
            catch (JsonSerializationException ex)
            {
                try
                {
                    DeserializeResult<string[]>(result);
                    return null;
                }
                catch
                {
                    throw ex;
                }
            }
        }

        public async Task<Dictionary<string, Sequence[]>> GetSequences()
        {
            var result = await Get("sequences/index.json").ConfigureAwait(false);
            try
            {
                return DeserializeResult<Dictionary<string, Sequence[]>>(result);
            }
            catch (JsonSerializationException ex)
            {
                try
                {
                    DeserializeResult<Sequence[][]>(result);
                    return null;
                }
                catch
                {
                    throw ex;
                }
            }
        }

        public async Task<Dictionary<int, string>> GetTags()
        {
            var result = await Get("tags/index.json").ConfigureAwait(false);
            try
            {
                return DeserializeResult<Dictionary<int, string>>(result);
            }
            catch (JsonSerializationException ex)
            {
                try
                {
                    DeserializeResult<string[]>(result);
                    return null;
                }
                catch
                {
                    throw ex;
                }
            }
        }

        public async Task<Logo[]> GetLogos()
        {
            var result = await Get("/users/logo").ConfigureAwait(false);
            return DeserializeResult<Logo[]>(result);
        }
        //was removed from api 16.05.2018
        //public async Task<Response<Register>> Register(string email, bool sendEmail = true)
        //{
        //    var result = await Post("/users/create", new
        //    {
        //        User = new User
        //        {
        //            Email = email,
        //            SendEmail = sendEmail
        //        }
        //    });
        //    return DeserializeResult<Response<Register>>(result);
        //}

        public async Task<Response<ExpandoObject>> CashRegister(int cashRegisterId, Request.PagedSearchParameters filter, bool listInfo = true)
        {
            var result = await Get(string.Format("/cash_register_items/index/{0}{1}", cashRegisterId, filter.ToParameters(listInfo)));
            return DeserializeResult<Response<ExpandoObject>>(result);
        }
    }


    public class SuperFakturaCZ : SuperFaktura
    {
        public SuperFakturaCZ(string email, string apiKey, string apptitle = null, string module = "API", int? companyId = null)
            : base(email, apiKey, apptitle, module, companyId)
        {
            APIURL = "https://moje.superfaktura.cz/";
        }
    }

    public class SuperFakturaAT : SuperFaktura
    {
        public SuperFakturaAT(string email, string apiKey, string apptitle = null, string module = "API", int? companyId = null)
            : base(email, apiKey, apptitle, module, companyId)
        {
            APIURL = "http://meine.superfaktura.at/";
        }
    }
}
