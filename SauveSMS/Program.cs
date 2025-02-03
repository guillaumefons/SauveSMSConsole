using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Exceptions;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

class AndroidSMSBackup
{
    static readonly string adbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "ADB", "adb.exe");
    static readonly ILogger<AndroidSMSBackup> Logger = LoggerFactory.Create(builder => builder.AddConsole().AddFile("logs/app-{Date}.txt")).CreateLogger<AndroidSMSBackup>();

    static void Main()
    {
        try
        {
            if (!File.Exists(adbPath))
            {
                Logger.LogError("Le fichier ADB n'a pas été trouvé à l'emplacement : {AdbPath}", adbPath);
                return;
            }

            if (!IsAdbServerRunning())
            {
                Logger.LogInformation("Le serveur ADB n'est pas en cours d'exécution. Démarrage du serveur...");
                StartAdbServer();
            }
            else
            {
                Logger.LogInformation("Le serveur ADB est déjà en cours d'exécution.");
            }

            var adbClient = new AdbClient();
            var device = adbClient.GetDevices().FirstOrDefault();

            if (device == null)
            {
                Logger.LogWarning("Aucun appareil Android connecté.");
                return;
            }

            Logger.LogInformation("Appareil détecté : {Model}", device.Model);

            string backupPath = CreateBackupFolder();
            BackupSMS(adbClient, device, backupPath);
        }
        catch (AdbException adbEx)
        {
            Logger.LogError(adbEx, "Erreur ADB : {Message}", adbEx.Message);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Une erreur inattendue s'est produite : {Message}", ex.Message);
        }
    }

    static bool IsAdbServerRunning()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = "devices",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return !output.Contains("* daemon not running");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Erreur lors de la vérification du statut du serveur ADB : {Message}", ex.Message);
            return false;
        }
    }

    static void StartAdbServer()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = "start-server",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
            Logger.LogInformation("Serveur ADB démarré avec succès.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Erreur lors du démarrage du serveur ADB : {Message}", ex.Message);
        }
    }

    static string CreateBackupFolder()
    {
        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string smsFolder = Path.Combine(userProfile, "Mes_SMS");

            if (!Directory.Exists(smsFolder))
            {
                Directory.CreateDirectory(smsFolder);
                Logger.LogInformation("Dossier créé : {Folder}", smsFolder);
            }

            return smsFolder;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Erreur lors de la création du dossier de sauvegarde : {Message}", ex.Message);
            throw;
        }
    }

    static void BackupSMS(AdbClient adbClient, DeviceData device, string backupPath)
    {
        string backupFile = Path.Combine(backupPath, $"sms_backup_{DateTime.Now:yyyyMMddHHmmss}.txt");

        try
        {
            var receiver = new ConsoleOutputReceiver();
            adbClient.ExecuteRemoteCommand("content query --uri content://sms", device, receiver);

            string output = receiver.ToString();
            string formattedSMS = FormatSMS(output);

            File.WriteAllText(backupFile, formattedSMS, Encoding.UTF8);
            Logger.LogInformation("Sauvegarde réussie : {BackupFile}", backupFile);
        }
        catch (AdbException adbEx)
        {
            Logger.LogError(adbEx, "Erreur lors de l'exécution de la commande ADB : {Message}", adbEx.Message);
        }
        catch (IOException ioEx)
        {
            Logger.LogError(ioEx, "Erreur lors de l'écriture du fichier de sauvegarde : {Message}", ioEx.Message);
        }
    }

    static string FormatSMS(string rawOutput)
    {
        var smsEntries = Regex.Split(rawOutput, @"Row: \d+\s+");
        var formattedOutput = new StringBuilder();

        foreach (var entry in smsEntries.Skip(1))
        {
            var date = ExtractValue(entry, "date");
            var address = ExtractValue(entry, "address");
            var type = ExtractValue(entry, "type");
            var body = ExtractFullBody(entry); // Utilisation de la fonction améliorée

            if (!string.IsNullOrEmpty(date) && !string.IsNullOrEmpty(address))
            {
                var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(date)).LocalDateTime;
                var formattedDate = dateTime.ToString("yyyy-MM-dd");
                var formattedTime = dateTime.ToString("HH:mm:ss");
                var direction = type == "1" ? "De" : "À";

                formattedOutput.AppendLine($"Date: {formattedDate}");
                formattedOutput.AppendLine($"Heure: {formattedTime}");
                formattedOutput.AppendLine($"{direction}: {address}");
                formattedOutput.AppendLine($"Message: {body}");
                formattedOutput.AppendLine();
            }
        }

        return formattedOutput.ToString();
    }

    static string ExtractValue(string input, string key)
    {
        var match = Regex.Match(input, $@"{key}=([^,\n]+)");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    static string ExtractFullBody(string input)
    {
        // Recherche du body entre guillemets
        var match = Regex.Match(input, @"body=""([^""]*)""", RegexOptions.Singleline);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        // Si pas de guillemets, on capture jusqu'à la prochaine virgule ou fin de ligne
        match = Regex.Match(input, @"body=([^,\n]*)", RegexOptions.Singleline);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return string.Empty;
    }
}
