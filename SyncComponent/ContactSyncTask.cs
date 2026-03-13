using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Collections.Generic;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Contacts;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Security.Credentials;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using System.IO;

namespace SyncComponent
{
    public sealed class ContactSyncTask : IBackgroundTask
    {
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();
            try
            {
                var syncManager = new SyncManager();
                // В фоне прогресс нам слушать не нужно
                await syncManager.SyncNowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ФОН] Ошибка: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    internal class SyncState
    {
        public Dictionary<string, string> Etags = new Dictionary<string, string>();
        public Dictionary<string, string> Hashes = new Dictionary<string, string>();
    }

    public sealed class SyncManager
    {
        // Теперь метод поддерживает отправку прогресса (строки)
        public IAsyncActionWithProgress<string> SyncNowAsync()
        {
            return AsyncInfo.Run<string>((token, progress) => ExecuteSyncAsync(progress));
        }

        private async Task ExecuteSyncAsync(IProgress<string> progress)
        {
            System.Diagnostics.Debug.WriteLine("=== НАЧАЛО ДВУСТОРОННЕЙ СИНХРОНИЗАЦИИ ===");
            progress?.Report("Инициализация: проверка ключей и токенов...");

            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            string clientId = localSettings.Values["GoogleClientId"] as string;
            string clientSecret = localSettings.Values["GoogleClientSecret"] as string;

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)) return;

            var vault = new PasswordVault();
            var credentials = vault.FindAllByResource("GoogleSyncApp").FirstOrDefault();
            if (credentials == null) return;
            credentials.RetrievePassword();
            string refreshToken = credentials.Password;

            string accessToken = await GetAccessTokenAsync(refreshToken, clientId, clientSecret);
            if (string.IsNullOrEmpty(accessToken)) return;

            var state = await LoadSyncStateAsync();
            var newEtags = new Dictionary<string, string>();
            var newHashes = new Dictionary<string, string>();

            progress?.Report("Скачивание контактов из Google...");
            var googleContacts = await FetchGoogleContactsAsync(accessToken);
            if (googleContacts == null) return;

            progress?.Report("Подключение к локальной телефонной книге...");
            var contactStore = await ContactManager.RequestStoreAsync(ContactStoreAccessType.AppContactsReadWrite);
            var contactLists = await contactStore.FindContactListsAsync();
            var myContactList = contactLists.FirstOrDefault(l => l.DisplayName == "Контакты Google");

            if (myContactList == null)
            {
                myContactList = await contactStore.CreateContactListAsync("Контакты Google");
                myContactList.OtherAppReadAccess = ContactListOtherAppReadAccess.Full;
                await myContactList.SaveAsync();
            }

            var syncedIds = await LoadSyncedIdsAsync();

            var existingContacts = new List<Contact>();
            var contactReader = myContactList.GetContactReader();
            var batch = await contactReader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                existingContacts.AddRange(batch.Contacts);
                batch = await contactReader.ReadBatchAsync();
            }

            int uploadedCount = 0; int downloadedCount = 0;
            int localDeletedCount = 0; int cloudDeletedCount = 0; int linkedCount = 0;

            // === ФАЗА 1: УДАЛЕНИЕ ИЗ GOOGLE ===
            int stepCounter = 0;
            var googleContactsList = googleContacts.ToList();
            foreach (var gc in googleContactsList)
            {
                stepCounter++;
                progress?.Report($"Сверка удалений в Google... {stepCounter}/{googleContactsList.Count}");

                var localMatch = existingContacts.FirstOrDefault(c => c.RemoteId == gc.Id);
                if (localMatch == null && state.Etags.ContainsKey(gc.Id))
                {
                    bool deleted = await DeleteGoogleContactAsync(accessToken, gc.Id);
                    if (deleted)
                    {
                        googleContacts.Remove(gc);
                        cloudDeletedCount++;
                        state.Etags.Remove(gc.Id);
                        state.Hashes.Remove(gc.Id);
                    }
                }
            }

            // === ФАЗА 2: УДАЛЕНИЕ ИЗ ТЕЛЕФОНА ===
            var toDeleteLocally = existingContacts.Where(c => !string.IsNullOrEmpty(c.RemoteId) && !googleContacts.Any(gc => gc.Id == c.RemoteId)).ToList();
            stepCounter = 0;
            foreach (var lc in toDeleteLocally)
            {
                stepCounter++;
                progress?.Report($"Очистка удаленных из телефона... {stepCounter}/{toDeleteLocally.Count}");
                await myContactList.DeleteContactAsync(lc);
                existingContacts.Remove(lc);
                localDeletedCount++;
            }

            // === ГЛАВНЫЙ ЦИКЛ СИНХРОНИЗАЦИИ И РАЗРЕШЕНИЯ КОНФЛИКТОВ ===
            stepCounter = 0;
            foreach (var gc in googleContacts)
            {
                stepCounter++;
                progress?.Report($"Синхронизация контактов... {stepCounter}/{googleContacts.Count}");

                var lc = existingContacts.FirstOrDefault(c => c.RemoteId == gc.Id);
                string googleHash = CalculateHash(gc);

                if (lc != null) // Контакт есть и там, и там. Проверяем, КТО изменился.
                {
                    string localHash = CalculateHashLocal(lc);
                    string lastHash = state.Hashes.ContainsKey(gc.Id) ? state.Hashes[gc.Id] : "";
                    string lastEtag = state.Etags.ContainsKey(gc.Id) ? state.Etags[gc.Id] : "";

                    bool isFirstV2Sync = string.IsNullOrEmpty(lastEtag);
                    bool cloudChanged = isFirstV2Sync || gc.ETag != lastEtag;

                    bool localChanged = !string.IsNullOrEmpty(lastHash) && localHash != lastHash;

                    if (cloudChanged)
                    {
                        await ApplyGoogleDataToLocalAsync(lc, gc, myContactList);
                        newHashes[gc.Id] = googleHash;
                        newEtags[gc.Id] = gc.ETag;
                        downloadedCount++;
                    }
                    else if (localChanged)
                    {
                        bool ok = await UpdateGoogleContactAsync(accessToken, lc, gc.ETag);
                        if (ok)
                        {
                            newHashes[gc.Id] = localHash;
                            newEtags[gc.Id] = gc.ETag;
                            uploadedCount++;
                        }
                        else
                        {
                            newHashes[gc.Id] = lastHash;
                            newEtags[gc.Id] = lastEtag;
                        }
                    }
                    else
                    {
                        newHashes[gc.Id] = googleHash;
                        newEtags[gc.Id] = gc.ETag;
                    }
                }
                else
                {
                    // НОВЫЙ КОНТАКТ ИЗ ОБЛАКА
                    var contact = new Contact { RemoteId = gc.Id };
                    await ApplyGoogleDataToLocalAsync(contact, gc, myContactList);

                    newEtags[gc.Id] = gc.ETag;
                    newHashes[gc.Id] = googleHash;
                    downloadedCount++;
                }
            }

            // === ВЫГРУЗКА НОВЫХ ЛОКАЛЬНЫХ ===
            var newLocals = existingContacts.Where(c => string.IsNullOrEmpty(c.RemoteId)).ToList();
            stepCounter = 0;
            foreach (var lc in newLocals)
            {
                stepCounter++;
                progress?.Report($"Выгрузка новых контактов в Google... {stepCounter}/{newLocals.Count}");

                string newId = await CreateGoogleContactAsync(accessToken, lc);
                if (!string.IsNullOrEmpty(newId))
                {
                    lc.RemoteId = newId;
                    await myContactList.SaveContactAsync(lc);
                    newHashes[newId] = CalculateHashLocal(lc);
                    uploadedCount++;
                }
            }

            progress?.Report("Сохранение состояния синхронизации...");
            await SaveSyncStateAsync(newEtags, newHashes);
            var newSyncedIds = existingContacts.Select(c => c.RemoteId).Where(id => !string.IsNullOrEmpty(id));
            await SaveSyncedIdsAsync(newSyncedIds);

            progress?.Report($"Готово! Скачано: {downloadedCount}, Обновлено(вверх): {uploadedCount}");
            System.Diagnostics.Debug.WriteLine($"=== ЗАВЕРШЕНО ===");
        }

        // Применяет распарсенные данные из Google к локальному объекту Contact
        private async Task ApplyGoogleDataToLocalAsync(Contact lc, GoogleContact gc, ContactList list)
        {
            lc.FirstName = gc.FirstName;
            lc.LastName = gc.LastName;
            lc.Notes = gc.Notes;

            lc.JobInfo.Clear();
            foreach (var org in gc.Organizations)
                lc.JobInfo.Add(new ContactJobInfo { CompanyName = org.Name, Title = org.Title });

            lc.Phones.Clear();
            foreach (var p in gc.Phones)
            {
                var kind = ContactPhoneKind.Other;
                if (p.Type.Contains("mobile")) kind = ContactPhoneKind.Mobile;
                else if (p.Type.Contains("home")) kind = ContactPhoneKind.Home;
                else if (p.Type.Contains("work")) kind = ContactPhoneKind.Work;
                lc.Phones.Add(new ContactPhone { Number = p.Number, Kind = kind });
            }

            lc.Emails.Clear();
            foreach (var e in gc.Emails)
            {
                var kind = ContactEmailKind.Other;
                if (e.Type.Contains("home")) kind = ContactEmailKind.Personal;
                else if (e.Type.Contains("work")) kind = ContactEmailKind.Work;
                lc.Emails.Add(new ContactEmail { Address = e.Address, Kind = kind });
            }

            lc.Addresses.Clear();
            foreach (var a in gc.Addresses)
            {
                var kind = a.Type.Contains("work") ? ContactAddressKind.Work : (a.Type.Contains("home") ? ContactAddressKind.Home : ContactAddressKind.Other);
                lc.Addresses.Add(new ContactAddress
                {
                    StreetAddress = a.Street,
                    Locality = a.City,
                    Region = a.Region,
                    PostalCode = a.PostalCode,
                    Country = a.Country,
                    Kind = kind
                });
            }

            lc.Websites.Clear();
            foreach (var w in gc.Urls)
            {
                if (string.IsNullOrWhiteSpace(w.Value)) continue;
                Uri result;
                if (Uri.TryCreate(w.Value, UriKind.Absolute, out result) && (result.Scheme == "http" || result.Scheme == "https"))
                    lc.Websites.Add(new ContactWebsite { Uri = result });
            }

            lc.ImportantDates.Clear();
            if (gc.Birthday != null)
            {
                uint m = gc.Birthday.Month; uint d = gc.Birthday.Day;
                if (m >= 1 && m <= 12 && d >= 1 && d <= 31)
                    lc.ImportantDates.Add(new ContactDate { Year = gc.Birthday.Year, Month = m, Day = d, Kind = ContactDateKind.Birthday });
            }

            if (!string.IsNullOrEmpty(gc.PhotoUrl) && !gc.PhotoUrl.Contains("default-user"))
            {
                try
                {
                    using (var hc = new HttpClient())
                    {
                        var stream = await hc.GetStreamAsync(gc.PhotoUrl);
                        var memStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                        var inputStream = System.IO.WindowsRuntimeStreamExtensions.AsInputStream(stream);
                        await Windows.Storage.Streams.RandomAccessStream.CopyAsync(inputStream, memStream.GetOutputStreamAt(0));
                        lc.SourceDisplayPicture = RandomAccessStreamReference.CreateFromStream(memStream);
                    }
                }
                catch { }
            }

            await list.SaveContactAsync(lc);
        }

        private string CleanString(string input) => (input ?? "").Trim().ToLower();

        private string CalculateHash(GoogleContact gc)
        {
            string bday = gc.Birthday != null ? $"{gc.Birthday.Year}-{gc.Birthday.Month}-{gc.Birthday.Day}" : "";
            string raw = $"{CleanString(gc.FirstName)}|{CleanString(gc.LastName)}|{CleanString(gc.Notes)}|" +
                         $"{string.Join(",", gc.Organizations.Select(o => CleanString(o.Name + o.Title)).OrderBy(o => o))}|" +
                         $"{string.Join(",", gc.Phones.Select(p => CleanPhone(p.Number) + ":" + p.Type).OrderBy(p => p))}|" +
                         $"{string.Join(",", gc.Emails.Select(e => CleanString(e.Address) + ":" + e.Type).OrderBy(e => e))}|" +
                         $"{string.Join(",", gc.Addresses.Select(a => CleanString(a.Street + a.City + a.Region + a.Country) + ":" + a.Type).OrderBy(a => a))}|" +
                         $"{string.Join(",", gc.Urls.Select(u => CleanString(u.Value)).OrderBy(u => u))}|{bday}";
            return ComputeSha1(raw);
        }

        private string CalculateHashLocal(Contact lc)
        {
            string bday = "";
            var bDate = lc.ImportantDates.FirstOrDefault(d => d.Kind == ContactDateKind.Birthday);
            if (bDate != null) bday = $"{bDate.Year}-{bDate.Month}-{bDate.Day}";

            string raw = $"{CleanString(lc.FirstName)}|{CleanString(lc.LastName)}|{CleanString(lc.Notes)}|" +
                         $"{string.Join(",", lc.JobInfo.Select(j => CleanString(j.CompanyName + j.Title)).OrderBy(j => j))}|" +
                         $"{string.Join(",", lc.Phones.Select(p => CleanPhone(p.Number) + ":" + MapUwpPhoneKind(p.Kind)).OrderBy(p => p))}|" +
                         $"{string.Join(",", lc.Emails.Select(e => CleanString(e.Address) + ":" + MapUwpEmailKind(e.Kind)).OrderBy(e => e))}|" +
                         $"{string.Join(",", lc.Addresses.Select(a => CleanString(a.StreetAddress + a.Locality + a.Region + a.Country) + ":" + MapUwpAddressKind(a.Kind)).OrderBy(a => a))}|" +
                         $"{string.Join(",", lc.Websites.Select(w => CleanString(w.Uri?.ToString())).OrderBy(u => u))}|{bday}";
            return ComputeSha1(raw);
        }

        private string CleanPhone(string phone)
        {
            if (string.IsNullOrEmpty(phone)) return "";

            // Удаляем все визуальные разделители
            return phone.Replace(" ", "")
                        .Replace("-", "")
                        .Replace("(", "")
                        .Replace(")", "")
                        .Replace("+", "");
        }

        // Помощники для маппинга типов в строку для хеша
        private string MapUwpPhoneKind(ContactPhoneKind k) => k == ContactPhoneKind.Mobile ? "mobile" : (k == ContactPhoneKind.Home ? "home" : (k == ContactPhoneKind.Work ? "work" : "other"));
        private string MapUwpEmailKind(ContactEmailKind k) => k == ContactEmailKind.Work ? "work" : (k == ContactEmailKind.Personal ? "home" : "other");
        private string MapUwpAddressKind(ContactAddressKind k) => k == ContactAddressKind.Work ? "work" : (k == ContactAddressKind.Home ? "home" : "other");

        private string ComputeSha1(string raw)
        {
            var buffer = Windows.Security.Cryptography.CryptographicBuffer.ConvertStringToBinary(raw, Windows.Security.Cryptography.BinaryStringEncoding.Utf8);
            var hashAlg = Windows.Security.Cryptography.Core.HashAlgorithmProvider.OpenAlgorithm(Windows.Security.Cryptography.Core.HashAlgorithmNames.Sha1);
            return Windows.Security.Cryptography.CryptographicBuffer.EncodeToHexString(hashAlg.HashData(buffer));
        }

        private async Task<bool> UpdateGoogleContactAsync(string accessToken, Contact localContact, string etag)
        {
            try
            {
                JsonObject person = BuildGoogleContactJson(localContact);
                person.SetNamedValue("etag", JsonValue.CreateStringValue(etag));

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    string resourceId = localContact.RemoteId;
                    if (!resourceId.StartsWith("people/")) resourceId = "people/" + resourceId;

                    string url = $"https://people.googleapis.com/v1/{resourceId}:updateContact?updatePersonFields=names,phoneNumbers,emailAddresses,addresses,urls,birthdays";

                    var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
                    {
                        Content = new StringContent(person.Stringify(), System.Text.Encoding.UTF8, "application/json")
                    };

                    var response = await client.SendAsync(request);
                    return response.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        private async Task<string> CreateGoogleContactAsync(string accessToken, Contact localContact)
        {
            try
            {
                JsonObject person = BuildGoogleContactJson(localContact);
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    var content = new StringContent(person.Stringify(), System.Text.Encoding.UTF8, "application/json");

                    var response = await client.PostAsync("https://people.googleapis.com/v1/people:createContact", content);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseJson = JsonObject.Parse(await response.Content.ReadAsStringAsync());
                        if (responseJson.ContainsKey("resourceName")) return responseJson.GetNamedString("resourceName");
                    }
                }
            }
            catch { }
            return null;
        }

        private JsonObject BuildGoogleContactJson(Contact lc)
        {
            JsonObject person = new JsonObject();

            JsonArray names = new JsonArray();
            JsonObject nameObj = new JsonObject();
            nameObj.SetNamedValue("givenName", JsonValue.CreateStringValue(lc.FirstName ?? ""));
            nameObj.SetNamedValue("familyName", JsonValue.CreateStringValue(lc.LastName ?? ""));
            names.Add(nameObj);
            person.SetNamedValue("names", names);

            if (!string.IsNullOrEmpty(lc.Notes))
            {
                JsonArray bios = new JsonArray();
                JsonObject bioObj = new JsonObject();
                bioObj.SetNamedValue("value", JsonValue.CreateStringValue(lc.Notes));
                bios.Add(bioObj);
                person.SetNamedValue("biographies", bios);
            }

            if (lc.JobInfo.Count > 0)
            {
                JsonArray orgs = new JsonArray();
                foreach (var job in lc.JobInfo)
                {
                    JsonObject orgObj = new JsonObject();
                    if (!string.IsNullOrEmpty(job.CompanyName)) orgObj.SetNamedValue("name", JsonValue.CreateStringValue(job.CompanyName));
                    if (!string.IsNullOrEmpty(job.Title)) orgObj.SetNamedValue("title", JsonValue.CreateStringValue(job.Title));
                    orgs.Add(orgObj);
                }
                person.SetNamedValue("organizations", orgs);
            }

            if (lc.Phones.Count > 0)
            {
                JsonArray phones = new JsonArray();
                foreach (var p in lc.Phones)
                {
                    if (!string.IsNullOrEmpty(p.Number))
                    {
                        string type = "other";
                        if (p.Kind == ContactPhoneKind.Mobile) type = "mobile";
                        else if (p.Kind == ContactPhoneKind.Home) type = "home";
                        else if (p.Kind == ContactPhoneKind.Work) type = "work";

                        JsonObject pObj = new JsonObject();
                        pObj.SetNamedValue("value", JsonValue.CreateStringValue(p.Number));
                        pObj.SetNamedValue("type", JsonValue.CreateStringValue(type));
                        phones.Add(pObj);
                    }
                }
                person.SetNamedValue("phoneNumbers", phones);
            }

            if (lc.Emails.Count > 0)
            {
                JsonArray emails = new JsonArray();
                foreach (var e in lc.Emails)
                {
                    if (!string.IsNullOrEmpty(e.Address))
                    {
                        string type = e.Kind == ContactEmailKind.Work ? "work" : (e.Kind == ContactEmailKind.Personal ? "home" : "other");
                        JsonObject eObj = new JsonObject();
                        eObj.SetNamedValue("value", JsonValue.CreateStringValue(e.Address));
                        eObj.SetNamedValue("type", JsonValue.CreateStringValue(type));
                        emails.Add(eObj);
                    }
                }
                person.SetNamedValue("emailAddresses", emails);
            }

            if (lc.Addresses.Count > 0)
            {
                JsonArray addresses = new JsonArray();
                foreach (var a in lc.Addresses)
                {
                    string type = a.Kind == ContactAddressKind.Work ? "work" : (a.Kind == ContactAddressKind.Home ? "home" : "other");
                    JsonObject aObj = new JsonObject();
                    aObj.SetNamedValue("type", JsonValue.CreateStringValue(type));
                    if (!string.IsNullOrEmpty(a.StreetAddress)) aObj.SetNamedValue("streetAddress", JsonValue.CreateStringValue(a.StreetAddress));
                    if (!string.IsNullOrEmpty(a.Locality)) aObj.SetNamedValue("city", JsonValue.CreateStringValue(a.Locality));
                    if (!string.IsNullOrEmpty(a.Region)) aObj.SetNamedValue("region", JsonValue.CreateStringValue(a.Region));
                    if (!string.IsNullOrEmpty(a.PostalCode)) aObj.SetNamedValue("postalCode", JsonValue.CreateStringValue(a.PostalCode));
                    if (!string.IsNullOrEmpty(a.Country)) aObj.SetNamedValue("country", JsonValue.CreateStringValue(a.Country));
                    addresses.Add(aObj);
                }
                person.SetNamedValue("addresses", addresses);
            }

            if (lc.Websites.Count > 0)
            {
                JsonArray urls = new JsonArray();
                foreach (var w in lc.Websites)
                {
                    if (w.Uri != null)
                    {
                        JsonObject uObj = new JsonObject();
                        uObj.SetNamedValue("value", JsonValue.CreateStringValue(w.Uri.ToString()));
                        uObj.SetNamedValue("type", JsonValue.CreateStringValue("other"));
                        urls.Add(uObj);
                    }
                }
                person.SetNamedValue("urls", urls);
            }

            var bDate = lc.ImportantDates.FirstOrDefault(d => d.Kind == ContactDateKind.Birthday);
            if (bDate != null)
            {
                JsonArray birthdays = new JsonArray();
                JsonObject dateObj = new JsonObject();
                JsonObject dateInner = new JsonObject();
                if (bDate.Year != null) dateInner.SetNamedValue("year", JsonValue.CreateNumberValue((double)bDate.Year));
                if (bDate.Month != null) dateInner.SetNamedValue("month", JsonValue.CreateNumberValue((double)bDate.Month));
                if (bDate.Day != null) dateInner.SetNamedValue("day", JsonValue.CreateNumberValue((double)bDate.Day));
                dateObj.SetNamedValue("date", dateInner);
                birthdays.Add(dateObj);
                person.SetNamedValue("birthdays", birthdays);
            }

            return person;
        }

        private async Task<List<GoogleContact>> FetchGoogleContactsAsync(string accessToken)
        {
            var contactsList = new List<GoogleContact>();

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                string nextPageToken = "";
                bool hasMorePages = true;

                while (hasMorePages)
                {
                    // ДОБАВЛЕНЫ: organizations, biographies
                    string requestUri = "https://people.googleapis.com/v1/people/me/connections?personFields=names,phoneNumbers,emailAddresses,addresses,urls,birthdays,photos,organizations,biographies&pageSize=1000";
                    if (!string.IsNullOrEmpty(nextPageToken)) requestUri += $"&pageToken={nextPageToken}";

                    var response = await client.GetAsync(requestUri);
                    if (response.IsSuccessStatusCode)
                    {
                        JsonObject json = JsonObject.Parse(await response.Content.ReadAsStringAsync());
                        if (json.ContainsKey("connections"))
                        {
                            JsonArray connections = json.GetNamedArray("connections");
                            foreach (var item in connections)
                            {
                                var person = item.GetObject();
                                var gc = new GoogleContact();

                                if (person.ContainsKey("resourceName")) gc.Id = person.GetNamedString("resourceName");
                                if (person.ContainsKey("etag")) gc.ETag = person.GetNamedString("etag");

                                if (person.ContainsKey("names"))
                                {
                                    var primaryName = person.GetNamedArray("names")[0].GetObject();
                                    if (primaryName.ContainsKey("givenName")) gc.FirstName = primaryName.GetNamedString("givenName");
                                    if (primaryName.ContainsKey("familyName")) gc.LastName = primaryName.GetNamedString("familyName");
                                }

                                if (person.ContainsKey("biographies"))
                                {
                                    var bios = person.GetNamedArray("biographies");
                                    if (bios.Count > 0 && bios[0].GetObject().ContainsKey("value"))
                                        gc.Notes = bios[0].GetObject().GetNamedString("value");
                                }

                                if (person.ContainsKey("organizations"))
                                {
                                    foreach (var oItem in person.GetNamedArray("organizations"))
                                    {
                                        var obj = oItem.GetObject();
                                        var org = new GOrg();
                                        if (obj.ContainsKey("name")) org.Name = obj.GetNamedString("name");
                                        if (obj.ContainsKey("title")) org.Title = obj.GetNamedString("title");
                                        if (!string.IsNullOrEmpty(org.Name) || !string.IsNullOrEmpty(org.Title)) gc.Organizations.Add(org);
                                    }
                                }

                                if (person.ContainsKey("phoneNumbers"))
                                {
                                    foreach (var pItem in person.GetNamedArray("phoneNumbers"))
                                    {
                                        var obj = pItem.GetObject();
                                        if (obj.ContainsKey("value")) gc.Phones.Add(new GPhone
                                        {
                                            Number = obj.GetNamedString("value"),
                                            Type = obj.ContainsKey("type") ? obj.GetNamedString("type").ToLower() : "other"
                                        });
                                    }
                                }

                                if (person.ContainsKey("emailAddresses"))
                                {
                                    foreach (var eItem in person.GetNamedArray("emailAddresses"))
                                    {
                                        var obj = eItem.GetObject();
                                        if (obj.ContainsKey("value")) gc.Emails.Add(new GEmail
                                        {
                                            Address = obj.GetNamedString("value"),
                                            Type = obj.ContainsKey("type") ? obj.GetNamedString("type").ToLower() : "other"
                                        });
                                    }
                                }

                                if (person.ContainsKey("addresses"))
                                {
                                    foreach (var aItem in person.GetNamedArray("addresses"))
                                    {
                                        var obj = aItem.GetObject();
                                        var addr = new GAddress();
                                        if (obj.ContainsKey("type")) addr.Type = obj.GetNamedString("type").ToLower();
                                        if (obj.ContainsKey("streetAddress")) addr.Street = obj.GetNamedString("streetAddress");
                                        if (obj.ContainsKey("city")) addr.City = obj.GetNamedString("city");
                                        if (obj.ContainsKey("region")) addr.Region = obj.GetNamedString("region");
                                        if (obj.ContainsKey("postalCode")) addr.PostalCode = obj.GetNamedString("postalCode");
                                        if (obj.ContainsKey("country")) addr.Country = obj.GetNamedString("country");

                                        if (!string.IsNullOrEmpty(addr.Street) || !string.IsNullOrEmpty(addr.City)) gc.Addresses.Add(addr);
                                    }
                                }

                                if (person.ContainsKey("urls"))
                                {
                                    foreach (var uItem in person.GetNamedArray("urls"))
                                    {
                                        var obj = uItem.GetObject();
                                        if (obj.ContainsKey("value")) gc.Urls.Add(new GUrl
                                        {
                                            Value = obj.GetNamedString("value"),
                                            Type = obj.ContainsKey("type") ? obj.GetNamedString("type").ToLower() : "other"
                                        });
                                    }
                                }

                                if (person.ContainsKey("birthdays"))
                                {
                                    var bdays = person.GetNamedArray("birthdays");
                                    if (bdays.Count > 0 && bdays[0].GetObject().ContainsKey("date"))
                                    {
                                        var dateObj = bdays[0].GetObject().GetNamedObject("date");
                                        gc.Birthday = new GDate();
                                        if (dateObj.ContainsKey("year")) gc.Birthday.Year = (int)dateObj.GetNamedNumber("year");
                                        if (dateObj.ContainsKey("month")) gc.Birthday.Month = (uint)dateObj.GetNamedNumber("month");
                                        if (dateObj.ContainsKey("day")) gc.Birthday.Day = (uint)dateObj.GetNamedNumber("day");
                                    }
                                }

                                if (person.ContainsKey("photos"))
                                {
                                    var photos = person.GetNamedArray("photos");
                                    if (photos.Count > 0 && photos[0].GetObject().ContainsKey("url"))
                                        gc.PhotoUrl = photos[0].GetObject().GetNamedString("url");
                                }

                                if (!string.IsNullOrEmpty(gc.FirstName) || gc.Phones.Count > 0 || gc.Emails.Count > 0)
                                {
                                    contactsList.Add(gc);
                                }
                            }
                        }

                        if (json.ContainsKey("nextPageToken")) nextPageToken = json.GetNamedString("nextPageToken");
                        else hasMorePages = false;
                    }
                    else hasMorePages = false;
                }
            }
            return contactsList;
        }

        // --- Вспомогательные системные методы остаются без изменений ---
        private async Task<string> GetAccessTokenAsync(string refreshToken, string clientId, string clientSecret)
        {
            using (var client = new HttpClient())
            {
                string requestBody = $"client_id={clientId}&client_secret={clientSecret}&refresh_token={refreshToken}&grant_type=refresh_token";
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
                var response = await client.PostAsync("https://oauth2.googleapis.com/token", content);
                if (response.IsSuccessStatusCode)
                {
                    JsonObject json = JsonObject.Parse(await response.Content.ReadAsStringAsync());
                    return json.GetNamedString("access_token");
                }
            }
            return null;
        }

        private async Task<bool> DeleteGoogleContactAsync(string accessToken, string resourceName)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    var response = await client.DeleteAsync($"https://people.googleapis.com/v1/{resourceName}:deleteContact");
                    return response.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        private async Task SaveSyncStateAsync(Dictionary<string, string> etags, Dictionary<string, string> hashes)
        {
            JsonObject root = new JsonObject();
            JsonObject etagsJson = new JsonObject(); JsonObject hashesJson = new JsonObject();
            foreach (var kvp in etags) etagsJson.SetNamedValue(kvp.Key, JsonValue.CreateStringValue(kvp.Value));
            foreach (var kvp in hashes) hashesJson.SetNamedValue(kvp.Key, JsonValue.CreateStringValue(kvp.Value));
            root.SetNamedValue("etags", etagsJson); root.SetNamedValue("hashes", hashesJson);
            var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync("sync_state_v2.json", Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(file, root.Stringify());
        }

        private async Task<SyncState> LoadSyncStateAsync()
        {
            var state = new SyncState();
            var item = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync("sync_state_v2.json");
            if (item != null)
            {

                var file = item as Windows.Storage.StorageFile;
                if (file != null)
                {
                    try
                    {
                        string jsonString = await Windows.Storage.FileIO.ReadTextAsync(file);
                        if (!string.IsNullOrEmpty(jsonString))
                        {
                            JsonObject root = JsonObject.Parse(jsonString);
                            if (root.ContainsKey("etags"))
                            {
                                var etagsObj = root.GetNamedObject("etags");
                                foreach (var key in etagsObj.Keys) state.Etags[key] = etagsObj.GetNamedString(key);
                            }
                            if (root.ContainsKey("hashes"))
                            {
                                var hashesObj = root.GetNamedObject("hashes");
                                foreach (var key in hashesObj.Keys) state.Hashes[key] = hashesObj.GetNamedString(key);
                            }
                        }
                    }
                    catch { }
                }


            }
            return state;
        }

        private async Task SaveSyncedIdsAsync(IEnumerable<string> ids)
        {
            var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync("synced_ids.txt", Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(file, string.Join(",", ids));
        }

        private async Task<HashSet<string>> LoadSyncedIdsAsync()
        {
            try
            {
                var file = await Windows.Storage.ApplicationData.Current.LocalFolder.GetFileAsync("synced_ids.txt");
                string content = await Windows.Storage.FileIO.ReadTextAsync(file);
                return new HashSet<string>(content.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            }
            
            catch { }
            return new HashSet<string>();
        }
}

    // Расширенный вспомогательный класс для новых полей
    internal class GoogleContact
    {
        public string Id { get; set; } = "";
        public string ETag { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Notes { get; set; } = ""; // Биографии/Заметки

        public List<GPhone> Phones { get; set; } = new List<GPhone>();
        public List<GEmail> Emails { get; set; } = new List<GEmail>();
        public List<GAddress> Addresses { get; set; } = new List<GAddress>();
        public List<GUrl> Urls { get; set; } = new List<GUrl>();
        public List<GOrg> Organizations { get; set; } = new List<GOrg>();

        public GDate Birthday { get; set; } = null;
        public string PhotoUrl { get; set; } = "";
    }

    internal class GPhone { public string Number { get; set; } = ""; public string Type { get; set; } = "other"; }
    internal class GEmail { public string Address { get; set; } = ""; public string Type { get; set; } = "other"; }
    internal class GUrl { public string Value { get; set; } = ""; public string Type { get; set; } = "other"; }
    internal class GOrg { public string Name { get; set; } = ""; public string Title { get; set; } = ""; }

    internal class GAddress
    {
        public string Street { get; set; } = "";
        public string City { get; set; } = "";
        public string Region { get; set; } = "";
        public string PostalCode { get; set; } = "";
        public string Country { get; set; } = "";
        public string Type { get; set; } = "home";
    }

    internal class GDate { public int? Year { get; set; } public uint Month { get; set; } public uint Day { get; set; } }
}