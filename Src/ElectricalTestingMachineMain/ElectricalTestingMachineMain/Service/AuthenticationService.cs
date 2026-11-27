using ElectricalTestingMachineMain.Model.Enum;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service
{
    public class AuthenticationService
    {
        private static readonly AuthenticationService _instance = new Lazy<AuthenticationService>(() => new AuthenticationService()).Value;
        public static AuthenticationService Instance => _instance;

        private readonly string _authFilePath;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        private const string DefaultPassword = "123456";

        public UserRole CurrentRole { get; private set; } = UserRole.Operator;
        public string CurrentUserName { get; private set; } = "作业员";
        public bool IsEngineer => CurrentRole == UserRole.Engineer;

        public event Action<UserRole>? RoleChanged;

        private AuthenticationService()
        {
            string processDir = AppContext.BaseDirectory;
            string configDir = Path.Combine(processDir, "Config");
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
            _authFilePath = Path.Combine(configDir, "AuthData.json");
        }

        public void Init()
        {
            LoadAuthData();
        }

        private void LoadAuthData()
        {
            if (!File.Exists(_authFilePath))
            {
                var defaultData = new AuthData
                {
                    EngineerPasswordHash = HashPassword(DefaultPassword)
                };
                SaveAuthData(defaultData);
                return;
            }

            try
            {
                string json = File.ReadAllText(_authFilePath);
                var data = JsonSerializer.Deserialize<AuthData>(json, _jsonOptions);
                if (data == null)
                {
                    var defaultData = new AuthData
                    {
                        EngineerPasswordHash = HashPassword(DefaultPassword)
                    };
                    SaveAuthData(defaultData);
                }
            }
            catch
            {
                var defaultData = new AuthData
                {
                    EngineerPasswordHash = HashPassword(DefaultPassword)
                };
                SaveAuthData(defaultData);
            }
        }

        private void SaveAuthData(AuthData data)
        {
            string? dir = Path.GetDirectoryName(_authFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(_authFilePath, json);
        }

        /// <summary>
        /// 以作业员身份登录（无需密码）
        /// </summary>
        public bool LoginAsOperator()
        {
            CurrentRole = UserRole.Operator;
            CurrentUserName = "作业员";
            RoleChanged?.Invoke(CurrentRole);
            return true;
        }

        /// <summary>
        /// 以工程师身份登录（需要密码验证）
        /// </summary>
        public bool LoginAsEngineer(string password)
        {
            if (string.IsNullOrWhiteSpace(password)) return false;

            try
            {
                string json = File.ReadAllText(_authFilePath);
                var data = JsonSerializer.Deserialize<AuthData>(json, _jsonOptions);
                if (data == null) return false;

                string inputHash = HashPassword(password);
                if (data.EngineerPasswordHash == inputHash)
                {
                    CurrentRole = UserRole.Engineer;
                    CurrentUserName = "工程师";
                    RoleChanged?.Invoke(CurrentRole);
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 修改工程师密码（需验证旧密码）
        /// </summary>
        public bool ChangePassword(string oldPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(oldPassword) || string.IsNullOrWhiteSpace(newPassword))
                return false;

            try
            {
                string json = File.ReadAllText(_authFilePath);
                var data = JsonSerializer.Deserialize<AuthData>(json, _jsonOptions);
                if (data == null) return false;

                string oldHash = HashPassword(oldPassword);
                if (data.EngineerPasswordHash != oldHash) return false;

                data.EngineerPasswordHash = HashPassword(newPassword);
                SaveAuthData(data);
                return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 登出
        /// </summary>
        public void Logout()
        {
            CurrentRole = UserRole.Operator;
            CurrentUserName = "作业员";
            RoleChanged?.Invoke(CurrentRole);
        }

        private static string HashPassword(string password)
        {
            using var sha256 = SHA256.Create();
            byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(bytes);
        }
    }

    public class AuthData
    {
        public string EngineerPasswordHash { get; set; } = string.Empty;
    }
}

