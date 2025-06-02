using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using HidLibrary;


namespace UPS
{
    public partial class Form1 : Form
    {
        // VID/PID Ippon Smart Winner II 3000 (Phoenixtec-чип)
        private const int UPS_VID = 0x06DA;
        private const int UPS_PID = 0x0003;

        private HidDevice hidDevice;
        private bool onBattery = false;         // Флаг: сейчас запитано от батареи
        private int remainingSeconds = 300;     // 5 минут = 300 секунд

        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            lblStatus.Text = "Статус: Инициализация...";
            lblTimer.Text = "Осталось: 05:00";

            // Пытаемся найти и открыть UPS по VID/PID
            hidDevice = HidDevices.Enumerate(UPS_VID, UPS_PID).FirstOrDefault();
            if (hidDevice == null)
            {
                MessageBox.Show(
                    "Не удалось найти Ippon Smart Winner II 3000.\n" +
                    "Убедитесь, что UPS подключен по USB и драйвер установлен.",
                    "Ошибка",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                lblStatus.Text = "Статус: UPS не найден";
                return;
            }

            // Открываем устройство
            hidDevice.OpenDevice();
            lblStatus.Text = "Статус: UPS найден, опрос запускается";

            // Запускаем таймер опроса UPS (каждую 1 сек)
            pollTimer.Start();
        }

        // Этот метод вызывается каждую секунду (pollTimer.Interval = 1000)
        private void pollTimer_Tick(object sender, EventArgs e)
        {
            // 1) Отправляем Megatec-команду "Q1\r" (Status query)
            int outReportLen = hidDevice.Capabilities.OutputReportByteLength;
            byte[] outBuffer = new byte[outReportLen];
            outBuffer[0] = 0x00; // Report ID = 0
            byte[] cmd = Encoding.ASCII.GetBytes("Q1\r");
            Array.Copy(cmd, 0, outBuffer, 1, cmd.Length);

            bool writeOK = hidDevice.WriteFeatureData(outBuffer);
            if (!writeOK)
            {
                lblStatus.Text = "Статус: Ошибка записи в UPS";
                return;
            }

            // 2) Читаем ответную Feature-оболочку с устройством
            // Согласно HidLibrary 2.x, ReadFeatureData должен вызываться с out-аргументом:
            //     bool ReadFeatureData(out byte[] data);
            // data будет содержать полный буфер (включая Report ID и паддинг).
            byte[] inBuffer;
            bool readOK = hidDevice.ReadFeatureData(out inBuffer);
            if (!readOK || inBuffer == null || inBuffer.Length == 0)
            {
                lblStatus.Text = "Статус: Ошибка чтения из UPS";
                return;
            }

            // 3) Преобразуем принятые байты в ASCII-строку
            // Обычно ответ выглядит как "(220.0  12.5  50.0  0000  23.0  50.0  0100)"
            // inBuffer может начинаться с Report ID в [0], дальше – ASCII-данные.
            string rawAscii = Encoding.ASCII.GetString(inBuffer)
                                 .Trim('\0')         // убираем все нулевые байты
                                 .Trim('\r', '\n');  // убираем символы конца строки
            // Если есть скобки, уберём их
            rawAscii = rawAscii.Trim('(', ')').Trim();

            // Разбиваем по пробелам – элементы: [0]=inputVoltage, [1]=outputVoltage, [2]=frequency и т.д.
            string[] parts = rawAscii.Split(
                new char[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries
            );
            if (parts.Length < 1)
            {
                lblStatus.Text = "Статус: Некорректный ответ UPS";
                return;
            }

            // Парсим первое значение – входное напряжение (Volts)
            if (!double.TryParse(parts[0], out double inputVoltage))
            {
                lblStatus.Text = "Статус: Не удалось распарсить напряжение UPS";
                return;
            }

            // 4) Определяем, перешли ли мы на батарею: 
            // often UPS присылает ~220 V (если сеть есть). Если < 50 V – значит сеть пропала.
            bool nowOnBattery = inputVoltage < 50.0;

            // Логика переходов:
            if (nowOnBattery && !onBattery)
            {
                // Только что перешли на батарею
                onBattery = true;
                remainingSeconds = 300;     // сброс таймера (5 минут)
                shutdownTimer.Start();      // запускаем таймер обратного отсчёта
                lblStatus.Text = "Статус: Питание от батареи. Таймер запущен.";
            }
            else if (!nowOnBattery && onBattery)
            {
                // Питание от сети вернулось до завершения таймера
                onBattery = false;
                shutdownTimer.Stop();
                lblStatus.Text = "Статус: Сеть восстановлена. Таймер сброшен.";
                lblTimer.Text = "Осталось: 05:00";
            }
            else if (nowOnBattery && onBattery)
            {
                // Всё ещё на батарее, таймер уже идёт
                lblStatus.Text = "Статус: На батарее. Предстоящее выключение через:";
                // lblTimer обновляется в shutdownTimer_Tick
            }
            else
            {
                // Питание от сети, таймер не был запущен
                lblStatus.Text = "Статус: Сеть есть";
                lblTimer.Text = "Осталось: 05:00";
            }
        }

        // Этот метод вызывается каждую секунду (shutdownTimer.Interval = 1000)
        private void shutdownTimer_Tick(object sender, EventArgs e)
        {
            if (remainingSeconds <= 0)
            {
                shutdownTimer.Stop();
                // Запускаем немедленное выключение Windows
                Process.Start("shutdown", "/s /t 0");
                return;
            }

            // Декремент таймера и обновление лейбла
            remainingSeconds--;
            int minutes = remainingSeconds / 60;
            int seconds = remainingSeconds % 60;
            lblTimer.Text = $"Осталось: {minutes:D2}:{seconds:D2}";
        }

        /// <summary>
        /// При выходе из формы закрываем HID-устройство, если оно было открыто.
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (hidDevice != null && hidDevice.IsOpen)
            {
                hidDevice.CloseDevice();
            }
        }
    }
}