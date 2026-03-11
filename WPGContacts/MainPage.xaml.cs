using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Contacts;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.UserDataAccounts;
using Windows.Data.Json;
using Windows.Security.Credentials;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace WPGContacts
{
    public sealed partial class MainPage : Page
    {
        // ВАШИ НОВЫЕ КЛЮЧИ ОТ ТИПА "TVs and Limited Input devices"
        //private const string ClientId = "152824189610-qr6octjqoo9elmreo6463a3qf8l4uqjl.apps.googleusercontent.com";
        //private const string ClientSecret = "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";

        private const string TaskName = "ContactSyncBackgroundTask";
        private const string TaskEntryPoint = "SyncComponent.ContactSyncTask";

        public MainPage()
        {
            this.InitializeComponent();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string clientId = ClientIdBox.Text.Trim();
            string clientSecret = ClientSecretBox.Text.Trim();

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                StatusText.Text = "Сначала введите Client ID и Client Secret!";
                return;
            }

            // Сохраняем ключи в локальные настройки для фоновой задачи
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            localSettings.Values["GoogleClientId"] = clientId;
            localSettings.Values["GoogleClientSecret"] = clientSecret;

            LoginButton.IsEnabled = false;
            StatusText.Text = "Связываемся с Google...";

            try
            {
                using (var client = new HttpClient())
                {
                    // Используем введенный clientId
                    string requestBody = $"client_id={clientId}&scope=https://www.googleapis.com/auth/contacts";
                    var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

                    var response = await client.PostAsync("https://oauth2.googleapis.com/device/code", content);
                    string jsonString = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        JsonObject json = JsonObject.Parse(jsonString);
                        string deviceCode = json.GetNamedString("device_code");
                        string userCode = json.GetNamedString("user_code");
                        string verificationUrl = json.GetNamedString("verification_url");
                        int interval = (int)json.GetNamedNumber("interval", 5);

                        CodePanel.Visibility = Visibility.Visible;
                        UserCodeText.Text = userCode;
                        StatusText.Text = "";

                        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                        dataPackage.SetText(userCode);
                        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                        StatusText.Text = "Откройте " + verificationUrl + " в современном браузере и введите код.";

                       //await Windows.System.Launcher.LaunchUriAsync(new Uri(verificationUrl));

                        // Передаем ключи в метод поллинга
                        StartPollingForToken(deviceCode, interval, clientId, clientSecret);
                    }
                    else
                    {
                        StatusText.Text = "Ошибка API: Проверьте правильность введенных ключей.";
                        LoginButton.IsEnabled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка: " + ex.Message;
                LoginButton.IsEnabled = true;
            }
        }

        private async void StartPollingForToken(string deviceCode, int intervalSeconds, string clientId, string clientSecret)
        {
            bool isWaiting = true;
            try
            {
                using (var client = new HttpClient())
                {
                    while (isWaiting)
                    {
                        await Task.Delay(intervalSeconds * 1000);

                        // Используем переданные ключи
                        string requestBody = $"client_id={clientId}&client_secret={clientSecret}&device_code={deviceCode}&grant_type=urn:ietf:params:oauth:grant-type:device_code";
                        var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

                        var response = await client.PostAsync("https://oauth2.googleapis.com/token", content);
                        string jsonString = await response.Content.ReadAsStringAsync();

                        if (response.IsSuccessStatusCode)
                        {
                            isWaiting = false;
                            JsonObject json = JsonObject.Parse(jsonString);
                            string refreshToken = json.GetNamedString("refresh_token");

                            await FinishSetupAsync(refreshToken);
                        }
                        else
                        {
                            JsonObject json = JsonObject.Parse(jsonString);
                            string error = json.ContainsKey("error") ? json.GetNamedString("error") : "";

                            if (error == "authorization_pending") continue;
                            else if (error == "slow_down") intervalSeconds += 2;
                            else
                            {
                                isWaiting = false;
                                StatusText.Text = "Ошибка авторизации: " + error;
                                CodePanel.Visibility = Visibility.Collapsed;
                                LoginButton.IsEnabled = true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка получения токена: " + ex.Message;
                CodePanel.Visibility = Visibility.Collapsed;
                LoginButton.IsEnabled = true;
            }
        }

        private async Task FinishSetupAsync(string refreshToken)
        {
            try
            {
                var vault = new PasswordVault();
                try
                {
                    var existingList = vault.FindAllByResource("GoogleSyncApp");
                    foreach (var existing in existingList) vault.Remove(existing);
                }
                catch (Exception) { }

                vault.Add(new PasswordCredential("GoogleSyncApp", "GoogleUser", refreshToken));

                await SetupWindowsContactStoreAsync();
                RegisterBackgroundTask();

                // ВМЕСТО простого изменения текста, вызываем смену интерфейса:
                ShowLoggedInUI();
                StatusText.Text = "Успешно! Аккаунт подключен.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка настройки контактов: " + ex.Message;
                ShowLoginUI();
            }
        }

        private async Task SetupWindowsContactStoreAsync()
        {
            try
            {
                // Запрашиваем доступ к хранилищу контактов. 
                // В старых SDK вызываем без параметров или с ContactStoreAccessType.AppContactsReadWrite
                var contactStore = await Windows.ApplicationModel.Contacts.ContactManager.RequestStoreAsync(
                    Windows.ApplicationModel.Contacts.ContactStoreAccessType.AppContactsReadWrite);

                // Проверяем, существует ли уже наш список
                var contactLists = await contactStore.FindContactListsAsync();
                var myContactList = contactLists.FirstOrDefault(l => l.DisplayName == "Контакты Google");

                if (myContactList == null)
                {
                    // СОЗДАЕМ СПИСОК БЕЗ УЧЕТНОЙ ЗАПИСИ
                    // Это создаст локальный список контактов, привязанный только к этому приложению.
                    // Для этого НЕ ТРЕБУЕТСЯ userDataAccounts.
                    myContactList = await contactStore.CreateContactListAsync("Контакты Google");

                    // Разрешаем другим системным приложениям (Люди, Почта) видеть эти контакты
                    myContactList.OtherAppReadAccess = Windows.ApplicationModel.Contacts.ContactListOtherAppReadAccess.Full;
                    await myContactList.SaveAsync();
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка инициализации хранилища: " + ex.Message;
                // Если здесь падает UnauthorizedAccessException, проверьте настройки конфиденциальности Windows 11
            }
        }

        private async void RegisterBackgroundTask()
        {
            var access = await BackgroundExecutionManager.RequestAccessAsync();
            var existingTask = BackgroundTaskRegistration.AllTasks.Values.FirstOrDefault(t => t.Name == TaskName);
            existingTask?.Unregister(true);

            var builder = new BackgroundTaskBuilder { Name = TaskName, TaskEntryPoint = TaskEntryPoint };
            builder.SetTrigger(new TimeTrigger(15, false));
            builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
            builder.Register();
        }


        // Вызывается автоматически при открытии приложения
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            CheckExistingLogin();
        }

        private void CheckExistingLogin()
        {
            var vault = new PasswordVault();
            try
            {
                // Пытаемся найти сохраненный токен
                var credentials = vault.FindAllByResource("GoogleSyncApp").FirstOrDefault();
                if (credentials != null)
                {
                    // Токен найден! Пользователь уже авторизован
                    ShowLoggedInUI();

                    // Убедимся, что фоновая задача зарегистрирована (на случай, если ОС её сбросила)
                    RegisterBackgroundTask();
                }
                else
                {
                    ShowLoginUI();
                }
            }
            catch (Exception)
            {
                // Хранилище пустое (первый запуск), показываем кнопку входа
                ShowLoginUI();
            }
        }

        // --- Управление видимостью элементов ---

        private void ShowLoggedInUI()
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            CodePanel.Visibility = Visibility.Collapsed;
            LoggedInPanel.Visibility = Visibility.Visible;
            StatusText.Text = "Синхронизация активна.";
        }

        private void ShowLoginUI()
        {
            LoginPanel.Visibility = Visibility.Visible;
            CodePanel.Visibility = Visibility.Collapsed;
            LoggedInPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "";
            LoginButton.IsEnabled = true;

            // Восстанавливаем введенные ранее ключи, если они есть
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (localSettings.Values.ContainsKey("GoogleClientId"))
                ClientIdBox.Text = localSettings.Values["GoogleClientId"].ToString();

            if (localSettings.Values.ContainsKey("GoogleClientSecret"))
                ClientSecretBox.Text = localSettings.Values["GoogleClientSecret"].ToString();
        }

        // --- Кнопка выхода из аккаунта ---

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            var vault = new PasswordVault();
            try
            {
                // Удаляем все токены Google из системы
                var credentials = vault.FindAllByResource("GoogleSyncApp");
                foreach (var c in credentials)
                {
                    vault.Remove(c);
                }
            }
            catch { } // Игнорируем ошибку, если уже пусто

            // Останавливаем и удаляем фоновую задачу
            var existingTask = BackgroundTaskRegistration.AllTasks.Values.FirstOrDefault(t => t.Name == TaskName);
            existingTask?.Unregister(true);

            // Возвращаем интерфейс к начальному виду
            ShowLoginUI();
            StatusText.Text = "Вы вышли из аккаунта. Фоновая синхронизация остановлена.";
        }


        // --- Кнопка ручной синхронизации ---
        private async void ManualSyncButton_Click(object sender, RoutedEventArgs e)
        {
            ManualSyncButton.IsEnabled = false;
            LogoutButton.IsEnabled = false;
            StatusText.Text = "Идет загрузка и сохранение контактов. Пожалуйста, подождите...";

            try
            {
                // Создаем экземпляр менеджера из нашего компонента
                var syncManager = new SyncComponent.SyncManager();

                // Выполняем синхронизацию
                await syncManager.SyncNowAsync();

                StatusText.Text = "Синхронизация успешно завершена! Контакты обновлены.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка ручной синхронизации: " + ex.Message;
            }
            finally
            {
                ManualSyncButton.IsEnabled = true;
                LogoutButton.IsEnabled = true;
            }
        }

    }
}