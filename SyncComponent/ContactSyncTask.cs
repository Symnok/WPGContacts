using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Contacts;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Security.Credentials;

namespace SyncComponent
{
    // Фоновая задача теперь просто вызывает наш менеджер
    public sealed class ContactSyncTask : IBackgroundTask
    {
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();
            try
            {
                var syncManager = new SyncManager();
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

    // Главный класс синхронизации, доступный для GUI
    public sealed class SyncManager
    {
        // ВАШИ КЛЮЧИ
        //private const string ClientId = "152824189610-qr6octjqoo9elmreo6463a3qf8l4uqjl.apps.googleusercontent.com";
        //private const string ClientSecret = "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";

        // Метод, совместимый с WinRT, который можно вызвать из MainPage
        public IAsyncAction SyncNowAsync()
        {
            return ExecuteSyncAsync().AsAsyncAction();
        }

        private async Task ExecuteSyncAsync()
        {
            System.Diagnostics.Debug.WriteLine("=== НАЧАЛО СИНХРОНИЗАЦИИ ===");

            // 1. Достаем ключи API из системных настроек
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            string clientId = localSettings.Values["GoogleClientId"] as string;
            string clientSecret = localSettings.Values["GoogleClientSecret"] as string;

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                System.Diagnostics.Debug.WriteLine("Отсутствуют Client ID или Client Secret. Отмена.");
                return;
            }

            // 2. Достаем токен
            var vault = new PasswordVault();
            var credentials = vault.FindAllByResource("GoogleSyncApp").FirstOrDefault();
            if (credentials == null) return;
            credentials.RetrievePassword();
            string refreshToken = credentials.Password;

            // 3. Получаем свежий Access Token (передаем ключи)
            string accessToken = await GetAccessTokenAsync(refreshToken, clientId, clientSecret);
            if (string.IsNullOrEmpty(accessToken))
            {
                System.Diagnostics.Debug.WriteLine("Не удалось получить Access Token.");
                return;
            }

            // 3. Скачиваем контакты
            var googleContacts = await FetchGoogleContactsAsync(accessToken);
            if (googleContacts == null || googleContacts.Count == 0) return;

            // 4. Подключаемся к системной телефонной книге
            var contactStore = await Windows.ApplicationModel.Contacts.ContactManager.RequestStoreAsync(
                Windows.ApplicationModel.Contacts.ContactStoreAccessType.AppContactsReadWrite);
            var contactLists = await contactStore.FindContactListsAsync();
            var myContactList = contactLists.FirstOrDefault(l => l.DisplayName == "Контакты Google");

            if (myContactList == null)
            {
                myContactList = await contactStore.CreateContactListAsync("Контакты Google");
                myContactList.OtherAppReadAccess = ContactListOtherAppReadAccess.Full;
                await myContactList.SaveAsync();
            }

            // 5. Очищаем старые контакты в этом списке, чтобы не было дубликатов при ручном запуске
            System.Diagnostics.Debug.WriteLine("Очистка старых контактов...");
            var contactReader = myContactList.GetContactReader();
            var existingContacts = new System.Collections.Generic.List<Contact>();
            var batch = await contactReader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                foreach (var oldContact in batch.Contacts)
                {
                    existingContacts.AddRange(batch.Contacts);
                }
                batch = await contactReader.ReadBatchAsync();
            }

            // 6. Сохраняем новые контакты и выводим их в консоль отладки
            System.Diagnostics.Debug.WriteLine($"Найдено контактов для добавления: {googleContacts.Count}");
            int addedCount = 0;
            foreach (var gc in googleContacts)
            {
                

                string newFullName = (gc.FirstName + " " + gc.LastName).Trim();
                string newPhone = gc.Phone ?? "";

                bool exists = existingContacts.Any(c =>
                    (c.FirstName + " " + c.LastName).Trim() == newFullName &&
                    (c.Phones.FirstOrDefault()?.Number ?? "") == newPhone
                    );

                if (!exists)
                {

                    var contact = new Contact
                    {
                        FirstName = gc.FirstName,
                        LastName = gc.LastName,
                        Notes = "Синхронизировано из Google"
                    };

                    if (!string.IsNullOrEmpty(gc.Phone))
                    {
                        contact.Phones.Add(new ContactPhone { Number = gc.Phone, Kind = ContactPhoneKind.Mobile });
                    }

                    await myContactList.SaveContactAsync(contact);
                    System.Diagnostics.Debug.WriteLine($"[+] Добавлен контакт: {gc.FirstName} {gc.LastName} | Тел: {gc.Phone}");
                    addedCount++;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[=] Пропуск (уже есть): {newFullName}");
                }
            }
            System.Diagnostics.Debug.WriteLine($"=== ЗАВЕРШЕНО. Добавлено новых: {addedCount} ===");
        }

        private async Task<string> GetAccessTokenAsync(string refreshToken, string clientId, string clientSecret)
        {
            using (var client = new HttpClient())
            {
                string requestBody = $"client_id={clientId}&client_secret={clientSecret}&refresh_token={refreshToken}&grant_type=refresh_token";
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

                var response = await client.PostAsync("https://oauth2.googleapis.com/token", content);
                if (response.IsSuccessStatusCode)
                {
                    string jsonString = await response.Content.ReadAsStringAsync();
                    JsonObject json = JsonObject.Parse(jsonString);
                    return json.GetNamedString("access_token");
                }
            }
            return null;
        }

        private async Task<System.Collections.Generic.List<GoogleContact>> FetchGoogleContactsAsync(string accessToken)
        {
            var contactsList = new System.Collections.Generic.List<GoogleContact>();

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                string requestUri = "https://people.googleapis.com/v1/people/me/connections?personFields=names,phoneNumbers";

                var response = await client.GetAsync(requestUri);
                if (response.IsSuccessStatusCode)
                {
                    string jsonString = await response.Content.ReadAsStringAsync();
                    JsonObject json = JsonObject.Parse(jsonString);

                    if (json.ContainsKey("connections"))
                    {
                        JsonArray connections = json.GetNamedArray("connections");
                        foreach (var item in connections)
                        {
                            var person = item.GetObject();
                            var parsedContact = new GoogleContact();

                            if (person.ContainsKey("names"))
                            {
                                var names = person.GetNamedArray("names");
                                if (names.Count > 0)
                                {
                                    var primaryName = names[0].GetObject();
                                    parsedContact.FirstName = primaryName.ContainsKey("givenName") ? primaryName.GetNamedString("givenName") : "";
                                    parsedContact.LastName = primaryName.ContainsKey("familyName") ? primaryName.GetNamedString("familyName") : "";
                                }
                            }

                            if (person.ContainsKey("phoneNumbers"))
                            {
                                var phones = person.GetNamedArray("phoneNumbers");
                                if (phones.Count > 0)
                                {
                                    parsedContact.Phone = phones[0].GetObject().GetNamedString("value");
                                }
                            }

                            if (!string.IsNullOrEmpty(parsedContact.FirstName) || !string.IsNullOrEmpty(parsedContact.Phone))
                            {
                                contactsList.Add(parsedContact);
                            }
                        }
                    }
                }
            }
            return contactsList;
        }
    }

    internal class GoogleContact
    {
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Phone { get; set; } = "";
    }
}