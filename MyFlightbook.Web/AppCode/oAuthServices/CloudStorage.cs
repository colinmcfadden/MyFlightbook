﻿using DotNetOpenAuth.OAuth2;
using Dropbox.Api;
using Microsoft.OneDrive.Sdk;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using MyFlightbook.OAuth;

/******************************************************
 * 
 * Copyright (c) 2016-2019 MyFlightbook LLC
 * Contact myflightbook-at-gmail.com for more information
 *
*******************************************************/

namespace MyFlightbook.CloudStorage
{
    /// <summary>
    /// Specifies the default cloud storage to use for a given user if they've authorized more than one.
    /// </summary>
    public enum StorageID { None, Dropbox, GoogleDrive, OneDrive, iCloud }

    /// <summary>
    /// Base class for cloud storage
    /// </summary>
    public abstract class CloudStorageBase : OAuthClientBase
    {
        /// <summary>
        /// Creates a cloud storage provider using parameters for oAuth authentication and token retrieval
        /// </summary>
        /// <param name="szAppKeyKey">The LocalConfig key for the oAuth2 appeky</param>
        /// <param name="szAppSecretKey">The LocalConfig key for the oAuth2 secret</param>
        /// <param name="szOAuth2AuthEndpoint">The oAuth2 authorization endpoint URL</param>
        /// <param name="szOAuth2TokenEndpoint">The oAuth2 token endpoint URL</param>
        /// <param name="scopes">Array of scopes for oAuth</param>
        protected CloudStorageBase(string szAppKeyKey, string szAppSecretKey, string szOAuth2AuthEndpoint, string szOAuth2TokenEndpoint, string[] scopes = null, string szUpgradeEndpoint = null, string szDisableEndpoint = null) : 
            base(szAppKeyKey, szAppSecretKey, szOAuth2AuthEndpoint, szOAuth2TokenEndpoint, scopes, szUpgradeEndpoint, szDisableEndpoint)
        {
        }

        public static string CloudStorageName(StorageID sid)
        {
            switch (sid)
            {
                case StorageID.Dropbox:
                    return Resources.LocalizedText.CloudStorageDropbox;
                case StorageID.GoogleDrive:
                    return Resources.LocalizedText.CloudStorageGDrive;
                case StorageID.iCloud:
                    return Resources.LocalizedText.CloudStorageICloud;
                case StorageID.OneDrive:
                    return Resources.LocalizedText.CloudStorageOneDrive;
                case StorageID.None:
                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>
    /// Provides utilities for using GoogleDrive from MyFlightbook
    /// </summary>
    public class GoogleDrive : CloudStorageBase
    {
        #region Errors
        /*
         * Generated by Xamasoft JSON Class Generator http://www.xamasoft.com/json-class-generator
         */
        [Serializable]
        public class GoogleDriveInternalError
        {

            [JsonProperty("domain")]
            public string domain { get; set; }

            [JsonProperty("reason")]
            public string reason { get; set; }

            [JsonProperty("message")]
            public string message { get; set; }
        }

        [Serializable]
        public class GoogleDriveError
        {

            [JsonProperty("errors")]
            public GoogleDriveInternalError[] errors { get; set; }

            [JsonProperty("code")]
            public int code { get; set; }

            [JsonProperty("message")]
            public string message { get; set; }
        }
        #endregion

        #region response data
        protected class GoogleDriveFileMetadata
        {
            public string kind { get; set; }
            public string id { get; set; }
            public string name { get; set; }
            public string mimeType { get; set; }
            public GoogleDriveFileMetadata() {  }
        }

        protected class GoogleFileList
        {
            public GoogleFileList() { }

            public string kind { get; set; }
            public GoogleDriveFileMetadata[] files {get; set;}
        }
        #endregion

        public const string szParamGDriveAuth = "gdOAuth";

        private const string szURLUploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart";
        private const string szURLUpdateEndpointTemplate = "https://www.googleapis.com/upload/drive/v3/files/{0}?uploadType=multipart";
        private const string szURLViewFilesEndpointTemplate = "https://www.googleapis.com/drive/v3/files?q={0}&access_token={1}";
        private string RootFolderID { get; set; }

        public GoogleDrive(string szRootPath = "")
            : base("GoogleDriveAccessID", "GoogleDriveClientSecret", "https://accounts.google.com/o/oauth2/v2/auth?access_type=offline&prompt=consent", "https://www.googleapis.com/oauth2/v4/token", new string[] { "https://www.googleapis.com/auth/drive.appdata", "https://www.googleapis.com/auth/drive.file" })
        {
            RootPath = String.IsNullOrEmpty(szRootPath) ? Branding.CurrentBrand.AppName : szRootPath;
            RootFolderID = string.Empty;
        }

        public GoogleDrive(IAuthorizationState authstate) : this(MyFlightbook.Branding.CurrentBrand.AppName)
        {
            AuthState = authstate;
        }

        public override AuthorizationState ConvertToken(HttpRequest Request)
        {
            if (Request == null)
                throw new ArgumentNullException("Request");

            HttpWebRequest hr = (HttpWebRequest)HttpWebRequest.Create(new Uri(oAuth2TokenEndpoint));
            hr.Method = "POST";
            hr.ContentType = "application/x-www-form-urlencoded";

            string szPostData = String.Format(CultureInfo.InvariantCulture, "code={0}&client_id={1}&client_secret={2}&redirect_uri={3}&grant_type=authorization_code",
                    Request["code"],
                    AppKey,
                    AppSecret,
                    RedirectUri(Request, Request.Path, szParamGDriveAuth).ToString());

            byte[] rgbData = System.Text.Encoding.UTF8.GetBytes(szPostData);
            hr.ContentLength = rgbData.Length;
            using (Stream s = hr.GetRequestStream())
            {
                s.Write(rgbData, 0, rgbData.Length);
            }

            WebResponse response = hr.GetResponse();
            using (StreamReader sr = new StreamReader(response.GetResponseStream()))
            {
                string result = sr.ReadToEnd();

                // JSonConvert can't deserialize space-delimited scopes into a hashset, so we need to do that manually.  Uggh.
                Dictionary<string, string> d = JsonConvert.DeserializeObject<Dictionary<string, string>>(result);

                AuthorizationState authstate = new AuthorizationState(d.ContainsKey("scope") ? OAuthUtilities.SplitScopes(d["scope"]) : null);
                authstate.AccessToken = d.ContainsKey("access_token") ? d["access_token"] : string.Empty;
                authstate.AccessTokenIssueDateUtc = DateTime.UtcNow;
                if (d.ContainsKey("expires_in"))
                {
                    int exp = 0;
                    if (int.TryParse(d["expires_in"], NumberStyles.Integer, CultureInfo.InvariantCulture, out exp))
                        authstate.AccessTokenExpirationUtc = DateTime.UtcNow.AddSeconds(exp);
                }
                authstate.RefreshToken = d.ContainsKey("refresh_token") ? d["refresh_token"] : string.Empty;

                return authstate;
            }
        }

        protected async Task<string> CreateFolder(string szFolderName)
        {
            HttpResponseMessage response = null;

            string szResult = string.Empty;

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + AuthState.AccessToken);

                // Create the metadata.  Name is most important, but we can also specify mimeType for CSV to import into GoogleDocs
                Dictionary<string, string> dictMeta = new Dictionary<string, string>() { { "name", szFolderName }, { "mimeType", "application/vnd.google-apps.folder" } };

                // Create the form.  The form itself needs the authtoken header
                using (MultipartContent form = new MultipartContent("related"))
                {
                    // Next add the metadata - it is in Json format
                    StringContent metadata = new StringContent(JsonConvert.SerializeObject(dictMeta));
                    metadata.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    form.Add(metadata);

                    try
                    {
                        response = await httpClient.PostAsync(szURLUploadEndpoint, form);
                        szResult = response.Content.ReadAsStringAsync().Result;
                        response.EnsureSuccessStatusCode();
                        if (!String.IsNullOrEmpty(szResult))
                        {
                            GoogleDriveFileMetadata gfm = JsonConvert.DeserializeObject<GoogleDriveFileMetadata>(szResult);
                            if (gfm != null)
                                return gfm.id;
                        }
                    }
                    catch (System.Net.Http.HttpRequestException ex)
                    {
                        if (response == null)
                            throw new MyFlightbookException("Unknown error in GoogleDrive.CreateFolder", ex);
                        else
                            throw new MyFlightbookException(response.ReasonPhrase + " " + szResult);
                    }
                }

                return string.Empty;
            }
        }

        #region Finding files and folders on GoogleDrive
        /// <summary>
        /// Returns the URL-based query for Google drive to look for a folder with the specified name
        /// </summary>
        /// <param name="szFolderName">The name of the folder</param>
        /// <returns>The ID of the resulting object (if found)</returns>
        protected string FolderQuery(string szFolderName)
        {
            return String.Format(System.Globalization.CultureInfo.InvariantCulture, "name%3D'{0}'+and+mimeType%3D'application%2Fvnd.google-apps.folder'", szFolderName);
        }

        /// <summary>
        /// Returns the URL-based query for Google drive to look for a file with the specified name
        /// </summary>
        /// <param name="szFileName">The name of the file</param>
        /// <param name="szParent">The ID of the parent folder</param>
        /// <returns>The ID of the resulting object (if found)</returns>
        protected string FileQuery(string szFileName, string szParent)
        {
            return String.Format(System.Globalization.CultureInfo.InvariantCulture, "name%3D'{0}'+and+'{1}'+in+parents+and+trashed%3Dfalse", szFileName, szParent);
        }

        /// <summary>
        /// Executes the specified search query, returning the ID of the first object that is found
        /// </summary>
        /// <param name="szQuery">Query (use FolderQuery or FileQuery)</param>
        /// <returns>The ID of the resulting object (if found), else string.empty</returns>
        protected async Task<string> FindIDForQuery(string szQuery)
        {
            // See if the folder exists
            string szURI = String.Format(System.Globalization.CultureInfo.InvariantCulture, szURLViewFilesEndpointTemplate, szQuery, AuthState.AccessToken);

            HttpResponseMessage response = null;

            using (HttpClient httpClient = new HttpClient())
            {
                try
                {
                    response = await httpClient.GetAsync(szURI);
                    response.EnsureSuccessStatusCode();
                    string szResult = response.Content.ReadAsStringAsync().Result;
                    if (!String.IsNullOrEmpty(szResult))
                    {
                        GoogleFileList gfl = JsonConvert.DeserializeObject<GoogleFileList>(szResult);
                        if (gfl != null && gfl.files.Length > 0)
                            return gfl.files[0].id;
                    }
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    if (response == null)
                        throw new MyFlightbookException("Unknown error in GoogleDrive.GetFolderID", ex);
                    else
                        throw new MyFlightbookException(response.ReasonPhrase);
                }
                catch { }
            }

            return string.Empty;
        }

        protected async Task<string> IDForFolder(string szFoldername)
        {
            return await FindIDForQuery(FolderQuery(szFoldername));
        }

        protected async Task<string> IDForFile(string szFileName, string szParentID)
        {
            return await FindIDForQuery(FileQuery(szFileName, szParentID));
        }
        #endregion

        /// <summary>
        /// Put's a file as an array of bytes
        /// </summary>
        /// <param name="szFileName">The file name to use</param>
        /// <param name="rgData">The array of bytes</param>
        /// <param name="szMimeType">The mime type for the data</param>
        /// <returns>True for success</returns>
        /// <exception cref="MyFlightbookException"></exception>
        public async Task<IReadOnlyDictionary<string, string>> PutFile(string szFileName, byte[] rgData, string szMimeType)
        {
            using (MemoryStream ms = new MemoryStream(rgData))
            {
                return await PutFile(ms, szFileName, szMimeType);
            }
        }

        /// <summary>
        /// Put's a file as a stream using the REST API documented at https://developers.google.com/drive/v3/web/manage-uploads#multipart
        /// </summary>
        /// <param name="szFileName">The file name to use</param>
        /// <param name="ms">The stream of the data</param>
        /// <param name="szMimeType">The mime type for the data</param>
        /// <returns>True for success</returns>
        /// <exception cref="MyFlightbookException"></exception>
        /// <exception cref="System.Net.Http.HttpRequestException"></exception>
        public async Task<IReadOnlyDictionary<string, string>> PutFile(Stream ms, string szFileName, string szMimeType)
        {
            if (!CheckAccessToken())
                throw new MyFlightbookException("Google drive: access token missing or expired");

            bool fIsCSV = szMimeType.CompareCurrentCultureIgnoreCase("text/csv") == 0;

            ms.Seek(0, SeekOrigin.Begin);   // write out the whole stream.  UploadAsync appears to pick up from the current location, which is the end-of-file after writing to a ZIP.

            string szResult = string.Empty;
            HttpResponseMessage response = null;

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + AuthState.AccessToken);

                if (String.IsNullOrEmpty(RootFolderID))
                {
                    RootFolderID = await IDForFolder(RootPath);
                    if (String.IsNullOrEmpty(RootFolderID))
                        RootFolderID = await CreateFolder(Branding.CurrentBrand.AppName);
                }

                // CSV loses its extension when uploaded because we map it to a google spreadsheet.  So if it's CSV AND we are patching an existing file, drop the extension so that we ov
                // update the existing file if it is present.  If CSV, strip the extension
                string szFileNameToCheck = fIsCSV ? Path.GetFileNameWithoutExtension(szFileName) : szFileName;
                string idExisting = null;
                if (!String.IsNullOrEmpty(RootFolderID))
                    idExisting = await IDForFile(szFileNameToCheck, RootFolderID);

                // If we got a hit, use that filename for the udpate
                if (!String.IsNullOrEmpty(idExisting))
                    szFileName = szFileNameToCheck;

                // Create the metadata.  Name is most important, but we can also specify mimeType for CSV to import into GoogleDocs
                Dictionary<string, object> dictMeta = new Dictionary<string, object>() { { "name", szFileName } };
                if (fIsCSV)
                    dictMeta["mimeType"] = "application/vnd.google-apps.spreadsheet";   // get it to show up in google drive sheets.
                if (String.IsNullOrEmpty(idExisting) && !String.IsNullOrEmpty(RootFolderID))
                    dictMeta["parents"] = new List<string>() { RootFolderID };

                // Create the form.  The form itself needs the authtoken header
                using (MultipartContent form = new MultipartContent("related"))
                {
                    // Next add the metadata - it is in Json format
                    string szJSonMeta = JsonConvert.SerializeObject(dictMeta);
                    StringContent metadata = new StringContent(szJSonMeta, System.Text.Encoding.UTF8);
                    metadata.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
                    form.Add(metadata);

                    // Finally, add the body, with its appropriate mime type.
                    StreamContent body = new StreamContent(ms);
                    body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(szMimeType);
                    form.Add(body);

                    try
                    {
                        response = (String.IsNullOrEmpty(idExisting)) ?
                            await httpClient.PostAsync(szURLUploadEndpoint, form) :
                            await httpClient.PatchAsync(new Uri(String.Format(szURLUpdateEndpointTemplate, idExisting)), form);
                        szResult = response.Content.ReadAsStringAsync().Result;
                        response.EnsureSuccessStatusCode();
                        return (String.IsNullOrEmpty(szResult)) ? null : JsonConvert.DeserializeObject<Dictionary<string, string>>(szResult);
                    }
                    catch (System.Net.Http.HttpRequestException ex)
                    {
                        if (response == null)
                            throw new MyFlightbookException("Unknown error in GoogleDrive.PutFile", ex);

                        Dictionary<string, GoogleDriveError> d = String.IsNullOrEmpty(szResult) ? null : JsonConvert.DeserializeObject<Dictionary<string, GoogleDriveError>>(szResult);
                        GoogleDriveError gde = (d == null || !d.ContainsKey("error")) ? null : d["error"];

                        if (gde != null && gde.code == 403 && gde.errors != null && gde.errors.Length > 0 && gde.errors[0].reason != null && gde.errors[0].reason.CompareCurrentCultureIgnoreCase("storageQuotaExceeded") == 0)
                            throw new MyFlightbookException(Resources.LocalizedText.GoogleDriveOutOfSpace);
                        else
                            throw new MyFlightbookException(response.ReasonPhrase + " " + (szResult ?? string.Empty));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Provides utilities for using OneDrive from MyFlightbook
    /// 
    /// Useful resources:
    ///   - oAuth flow and endpoints: https://dev.onedrive.com/auth/msa_oauth.htm
    /// </summary>
    public class OneDrive : CloudStorageBase
    {
        public const string TokenSessionKey = "sessionkeyforonedrive";
        public const string szParam1DriveAuth = "1dOAuth";

        protected IOneDriveClient Client { get; set; }

        public OneDrive(string szRootPath = "") 
            : base("OneDriveAccessID", "OneDriveClientSecret", "https://login.live.com/oauth20_authorize.srf", "https://login.live.com/oauth20_token.srf", new string[] { "onedrive.appfolder", "wl.basic", "onedrive.readwrite", "wl.offline_access" })
        {
            RootPath = szRootPath;
        }

        public OneDrive(IAuthorizationState authstate) : this(MyFlightbook.Branding.CurrentBrand.AppName + "/")
        {
            AuthState = authstate;
        }

        public async Task<IOneDriveClient> InitClient()
        {
            // SimpleOneDriveAuthProvider auth = new SimpleOneDriveAuthProvider() { AccessToken = authstate.AccessToken };
            return await OneDriveClient.GetSilentlyAuthenticatedMicrosoftAccountClient(AppKey, string.Empty, Scopes, AppSecret, AuthState.RefreshToken);
        }

        /// <summary>
        /// Generates a human-readable message for the user from a OneDriveException.
        /// </summary>
        /// <param name="ex">The Exception</param>
        /// <returns>A human-readable message</returns>
        public static string MessageForException(OneDriveException ex)
        {
            /*
             * 
             *  See also https://dev.onedrive.com/misc/errors.htm
             *  
             *     public enum OneDriveErrorCode
    {
        AccessDenied = 0,
        ActivityLimitReached = 1,
        AuthenticationCancelled = 2,
        AuthenticationFailure = 3,
        GeneralException = 4,
        InvalidRange = 5,
        InvalidRequest = 6,
        ItemNotFound = 7,
        MalwareDetected = 8,
        MyFilesCapabilityNotFound = 9,
        NameAlreadyExists = 10,
        NotAllowed = 11,
        NotSupported = 12,
        ResourceModified = 13,
        ResyncRequired = 14,
        ServiceNotAvailable = 15,
        Timeout = 16,
        TooManyRedirects = 17,
        QuotaLimitReached = 18,
        Unauthenticated = 19,
        UserDoesNotHaveMyFilesService = 20
    }
    */
            if (ex == null)
                return string.Empty;
            if (ex.IsMatch(OneDriveErrorCode.AccessDenied.ToString()) || ex.IsMatch(OneDriveErrorCode.AuthenticationCancelled.ToString()) || ex.IsMatch(OneDriveErrorCode.AuthenticationFailure.ToString()) || ex.IsMatch(OneDriveErrorCode.Unauthenticated.ToString()))
                return Branding.ReBrand(Resources.LocalizedText.OneDriveBadAuth);
            else if (ex.IsMatch(OneDriveErrorCode.QuotaLimitReached.ToString()))
                return Resources.LocalizedText.OneDriveErrorOutOfSpace;
            else if (ex.IsMatch(OneDriveErrorCode.Timeout.ToString()) || ex.IsMatch(OneDriveErrorCode.ServiceNotAvailable.ToString()))
                return Resources.LocalizedText.OneDriveCantReachService;
            else
                return ex.Message;
        }

        /// <summary>
        /// Put's a file as an array of bytes
        /// </summary>
        /// <param name="szFileName">The file name to use</param>
        /// <param name="rgData">The array of bytes</param>
        /// <returns>The resulting item</returns>
        /// <exception cref="OneDriveException"></exception>
        public async Task<Item> PutFile(string szFileName, byte[] rgData)
        {
            using (MemoryStream ms = new MemoryStream(rgData))
            {
                return await PutFile(ms, szFileName);
            }
        }

        /// <summary>
        /// Put's a file as a stream
        /// </summary>
        /// <param name="szFileName">The file name to use</param>
        /// <param name="ms">The stream of the data</param>
        /// <returns>FileMetadata with the result</returns>
        /// <exception cref="OneDriveException"></exception>
        public async Task<Item> PutFile(Stream ms, string szFileName)
        {
            if (Client == null)
            {
                try
                {
                    Client = await InitClient();
                }
                catch (OneDriveException ex)    // the exception here has no useful message, but it's clearly an auth issue, so map it to that.
                {
                    throw new MyFlightbookException(Branding.ReBrand(Resources.LocalizedText.OneDriveBadAuth), ex);
                }
            }

            ms.Seek(0, SeekOrigin.Begin);   // write out the whole stream.  UploadAsync appears to pick up from the current location, which is the end-of-file after writing to a ZIP.
            var uploadedItem = await Client.Drive.Root.ItemWithPath(RootPath + szFileName).Content.Request().PutAsync<Item>(ms);
            return uploadedItem;
        }
    }

    /// <summary>
    /// Provides utilities for using Dropbox from MyFlightbook
    /// </summary>
    public class MFBDropbox: CloudStorageBase
    {
        public enum TokenStatus { None, oAuth1, oAuth2 }
        public const string szParamDropboxAuth = "dbOAuth";

        public MFBDropbox()
            : base("DropboxAccessID", "DropboxClientSecret", "https://www.dropbox.com/oauth2/authorize", "https://api.dropboxapi.com/oauth2/token", null, "https://api.dropboxapi.com/2/auth/token/from_oauth1", "https://api.dropboxapi.com/2/auth/token/revoke")
        {
        }

        /// <summary>
        /// Determines the type of dropbox oAuth token we have.  Optionally upgrades to an oAuth 2.0 credential and/or disables the existing one.
        /// MODIFIES THE IN MEMORY USER PROFILE to use an upgraded credential
        /// </summary>
        /// <param name="pf">The user profile</param>
        /// <param name="fCommit">True to update the database with the oAuth 2.0 credential</param>
        /// <param name="fDisable">True to disable the old oAuth 1.0</param>
        /// <returns>The state of the dropbox access token PRIOR to upgrade.</returns>
        async public Task<TokenStatus> ValidateDropboxToken(MyFlightbook.Profile pf, bool fCommit = false, bool fDisable = false)
        {
            TokenStatus result = TokenStatus.None;

            if (pf == null || String.IsNullOrEmpty(pf.DropboxAccessToken))
                return result;

            try
            {
                string dbAppKey = AppKey;
                string dbSecret = AppSecret;

                byte[] rgbOAuth1Token = Convert.FromBase64String(pf.DropboxAccessToken);
                string xmlOAuth1Token = System.Text.Encoding.Default.GetString(rgbOAuth1Token);
                // if we get here, it is probably an oAuth1 token

                if (xmlOAuth1Token.Trim().StartsWith("<", StringComparison.OrdinalIgnoreCase))
                {
                    string szRawToken = null;
                    string szRawSecret = null;

                    using (MemoryStream stream = new MemoryStream(rgbOAuth1Token))
                    {
                        System.Runtime.Serialization.DataContractSerializer serializer = new System.Runtime.Serialization.DataContractSerializer(typeof(Dictionary<string, string>));
                        Object o = serializer.ReadObject(stream);
                        if (o.GetType().Equals(typeof(Dictionary<string, string>)))
                        {
                            Dictionary<string, string> d = (Dictionary<string, string>)o;
                            szRawToken = d["TokenDropBoxUsername"];
                            szRawSecret = d["TokenDropBoxPassword"];
                        }
                    }


                    try
                    {
                        DropboxAppClient client = new DropboxAppClient(dbAppKey, dbSecret);
                        var tokenFromOAuth1Result = await client.Auth.TokenFromOauth1Async(szRawToken, szRawSecret);
                        pf.DropboxAccessToken = tokenFromOAuth1Result.Oauth2Token;

                        if (fCommit)
                            pf.FCommit();

                        result = TokenStatus.oAuth1;
                    }
                    catch (WebException ex)
                    {
                        Stream ResponseStream = ex.Response.GetResponseStream();
                        StreamReader reader = new System.IO.StreamReader(ResponseStream, System.Text.Encoding.Default);
                        string szResult = reader.ReadToEnd();
                    }
                }
                else
                    result = TokenStatus.oAuth2;
            }
            catch (FormatException)
            {
                // It should be v2!
                result = TokenStatus.oAuth2;
            }
            return result;
        }

        /// <summary>
        /// Put's a file as an array of bytes
        /// </summary>
        /// <param name="szDropboxAccessToken">The oAuth 2.0 access token</param>
        /// <param name="szFileName">The file name to use</param>
        /// <param name="rgData">The array of bytes</param>
        /// <returns>FileMetadata with the result</returns>
        public async static Task<Dropbox.Api.Files.FileMetadata> PutFile(string szDropboxAccessToken, string szFileName, byte[] rgData)
        {
            using (MemoryStream ms = new MemoryStream(rgData))
            {
                return await PutFile(szDropboxAccessToken, ms, szFileName);
            }
        }

        /// <summary>
        /// Put's a file as a stream
        /// </summary>
        /// <param name="szDropboxAccessToken">The oAuth 2.0 access token</param>
        /// <param name="szFileName">The file name to use</param>
        /// <param name="ms">The stream of the data</param>
        /// <returns>FileMetadata with the result</returns>
        public async static Task<Dropbox.Api.Files.FileMetadata> PutFile(string szDropboxAccessToken, Stream ms, string szFileName)
        {
            ms.Seek(0, SeekOrigin.Begin);   // write out the whole stream.  UploadAsync appears to pick up from the current location, which is the end-of-file after writing to a ZIP.
            DropboxClient dbx = new DropboxClient(szDropboxAccessToken);
            Dropbox.Api.Files.FileMetadata updated = await dbx.Files.UploadAsync("/" + szFileName, Dropbox.Api.Files.WriteMode.Overwrite.Instance, body: ms);
            return updated;
        }
    }
}