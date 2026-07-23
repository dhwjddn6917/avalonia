using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace MobileEssControl.Services.Dialogs;

public static class AppDialogService
{
    public static async Task ShowWarningAsync(
        string title,
        string message)
    {
        Window? owner = null;

        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
        {
            owner = desktop.MainWindow;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 240,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#991B1B")),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var messageText = new TextBlock
        {
            Text = message,
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.Parse("#334155")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20)
        };

        var okButton = new Button
        {
            Content = "확인",
            Width = 110,
            Height = 42,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.Parse("#111827")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#111827")),
            FontWeight = FontWeight.Bold
        };

        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Border
        {
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    titleText,
                    messageText,
                    okButton
                }
            }
        };

        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    /// <summary>
    /// 터치 화면에서 사용하는 숫자 비밀번호 입력창입니다.
    /// 확인 시 입력된 숫자를 반환하고, 취소 또는 X로 닫으면 null을 반환합니다.
    /// </summary>
    public static async Task<string?> ShowNumericPasswordAsync(
        string title,
        string message,
        int maxLength = 4)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLength),
                "비밀번호 길이는 1자리 이상이어야 합니다.");
        }

        Window? owner = null;

        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
        {
            owner = desktop.MainWindow;
        }

        string input = string.Empty;
        string? result = null;

        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 540,
            MinWidth = 360,
            MinHeight = 540,
            MaxWidth = 360,
            MaxHeight = 540,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(
                Color.Parse("#0F172A")),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var messageText = new TextBlock
        {
            Text = message,
            FontSize = 14,
            Foreground = new SolidColorBrush(
                Color.Parse("#475569")),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var passwordDisplay = new TextBlock
        {
            FontSize = 30,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(
                Color.Parse("#0F172A")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var displayBorder = new Border
        {
            Height = 62,
            Background = new SolidColorBrush(
                Color.Parse("#F8FAFC")),
            BorderBrush = new SolidColorBrush(
                Color.Parse("#CBD5E1")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = passwordDisplay
        };

        var keypad = new Grid
        {
            Width = 304,
            HorizontalAlignment = HorizontalAlignment.Center,
            RowDefinitions =
                new RowDefinitions("56,56,56,56"),
            ColumnDefinitions =
                new ColumnDefinitions("96,96,96"),
            RowSpacing = 8,
            ColumnSpacing = 8
        };

        var cancelButton = new Button
        {
            Content = "취소",
            Height = 46,
            Background = new SolidColorBrush(
                Color.Parse("#E2E8F0")),
            Foreground = new SolidColorBrush(
                Color.Parse("#334155")),
            BorderBrush = new SolidColorBrush(
                Color.Parse("#CBD5E1")),
            FontSize = 17,
            FontWeight = FontWeight.Bold
        };

        var confirmButton = new Button
        {
            Content = "확인",
            Height = 46,
            Background = new SolidColorBrush(
                Color.Parse("#2563EB")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(
                Color.Parse("#2563EB")),
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            IsEnabled = false
        };

        void UpdateDisplay()
        {
            string filled =
                new string('●', input.Length);

            string empty =
                new string(
                    '○',
                    Math.Max(0, maxLength - input.Length));

            passwordDisplay.Text =
                string.Join(
                    "  ",
                    (filled + empty).ToCharArray());

            confirmButton.IsEnabled =
                input.Length == maxLength;
        }

        Button CreateKeyButton(
            string text,
            Action clickAction,
            string background = "#F8FAFC",
            string foreground = "#0F172A")
        {
            var button = new Button
            {
                Content = text,
                Width = 96,
                Height = 56,
                MinWidth = 96,
                MaxWidth = 96,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,

                // 버튼 안의 숫자와 글자를 정중앙에 배치
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,

                Background = new SolidColorBrush(
                    Color.Parse(background)),
                Foreground = new SolidColorBrush(
                    Color.Parse(foreground)),
                BorderBrush = new SolidColorBrush(
                    Color.Parse("#CBD5E1")),
                FontSize = text == "전체삭제" ? 16 : 24,
                FontWeight = FontWeight.Bold,
                CornerRadius = new CornerRadius(12)
            };

            button.Click += (_, _) =>
                clickAction();

            return button;
        }

        void AddKey(
            Control control,
            int row,
            int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            keypad.Children.Add(control);
        }

        void AddDigit(
            string digit,
            int row,
            int column)
        {
            AddKey(
                CreateKeyButton(
                    digit,
                    () =>
                    {
                        if (input.Length >= maxLength)
                        {
                            return;
                        }

                        input += digit;
                        UpdateDisplay();
                    }),
                row,
                column);
        }

        AddDigit("1", 0, 0);
        AddDigit("2", 0, 1);
        AddDigit("3", 0, 2);

        AddDigit("4", 1, 0);
        AddDigit("5", 1, 1);
        AddDigit("6", 1, 2);

        AddDigit("7", 2, 0);
        AddDigit("8", 2, 1);
        AddDigit("9", 2, 2);

        AddKey(
            CreateKeyButton(
                "전체삭제",
                () =>
                {
                    input = string.Empty;
                    UpdateDisplay();
                },
                background: "#FFF7ED",
                foreground: "#C2410C"),
            3,
            0);

        AddDigit("0", 3, 1);

        AddKey(
            CreateKeyButton(
                "←",
                () =>
                {
                    if (input.Length == 0)
                    {
                        return;
                    }

                    input =
                        input.Substring(
                            0,
                            input.Length - 1);

                    UpdateDisplay();
                },
                background: "#EFF6FF",
                foreground: "#1D4ED8"),
            3,
            2);

        cancelButton.Click += (_, _) =>
        {
            result = null;
            dialog.Close();
        };

        confirmButton.Click += (_, _) =>
        {
            if (input.Length != maxLength)
            {
                return;
            }

            result = input;
            dialog.Close();
        };

        var actionButtons = new Grid
        {
            ColumnDefinitions =
                new ColumnDefinitions("*,*"),
            ColumnSpacing = 12
        };

        Grid.SetColumn(cancelButton, 0);
        Grid.SetColumn(confirmButton, 1);

        actionButtons.Children.Add(cancelButton);
        actionButtons.Children.Add(confirmButton);

        dialog.Content = new Border
        {
            Padding = new Thickness(20),
            Child = new Grid
            {
                RowDefinitions =
                    new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
                RowSpacing = 12,
                Children =
                {
                    titleText,

                    new Border
                    {
                        Child = messageText,
                        [Grid.RowProperty] = 1
                    },

                    new Border
                    {
                        Child = displayBorder,
                        [Grid.RowProperty] = 2
                    },

                    new Border
                    {
                        Child = keypad,
                        [Grid.RowProperty] = 3
                    },

                    new Border
                    {
                        Child = actionButtons,
                        [Grid.RowProperty] = 4
                    }
                }
            }
        };

        UpdateDisplay();

        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var completion =
                new TaskCompletionSource<bool>();

            dialog.Closed += (_, _) =>
                completion.TrySetResult(true);

            dialog.Show();
            await completion.Task;
        }

        return result;
    }

    public static async Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText = "시작",
        string cancelText = "취소",
        double dialogWidth = 460,
        double dialogHeight = 280)
    {
        Window? owner = null;

        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
        {
            owner = desktop.MainWindow;
        }

        bool result = false;

        var dialog = new Window
        {
            Title = title,
            Width = dialogWidth,
            Height = dialogHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(
                Color.Parse("#0F172A")),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var messageText = new TextBlock
        {
            Text = message,
            FontSize = 15,
            Foreground = new SolidColorBrush(
                Color.Parse("#334155")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 23,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var cancelButton = new Button
        {
            Content = cancelText,
            Width = 120,
            Height = 44,
            Background = new SolidColorBrush(
                Color.Parse("#E2E8F0")),
            Foreground = new SolidColorBrush(
                Color.Parse("#334155")),
            BorderBrush = new SolidColorBrush(
                Color.Parse("#CBD5E1")),
            FontWeight = FontWeight.Bold
        };

        var confirmButton = new Button
        {
            Content = confirmText,
            Width = 120,
            Height = 44,
            Background = new SolidColorBrush(
                Color.Parse("#2563EB")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(
                Color.Parse("#2563EB")),
            FontWeight = FontWeight.Bold
        };

        cancelButton.Click += (_, _) =>
        {
            result = false;
            dialog.Close();
        };

        confirmButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        dialog.Closed += (_, _) =>
        {
            // 우측 상단 X 버튼으로 닫았을 때도 취소로 처리
            if (!result)
            {
                result = false;
            }
        };

        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Children =
            {
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        titleText,
                        messageText
                    }
                },

                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 12,
                    Children =
                    {
                        cancelButton,
                        confirmButton
                    },

                    [Grid.RowProperty] = 1
                }
            }
            }
        };

        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();

            var completion =
                new TaskCompletionSource<bool>();

            dialog.Closed += (_, _) =>
                completion.TrySetResult(result);

            await completion.Task;
        }

        return result;
    }
}