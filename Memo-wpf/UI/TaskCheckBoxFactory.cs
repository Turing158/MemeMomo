using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using System.Windows.Markup;
using System.Windows.Media;

namespace Memo.UI;

/// <summary>
/// Creates the inline task CheckBox used inside Markdown previews. Instead of
/// scaling its frame, the template applies press/release motion to the fill and
/// glyph while hover only changes the fill color.
/// </summary>
public static class TaskCheckBoxFactory
{
    private const double CheckedPressedScale = 0.7;
    private const double DefaultPressedScale = 0.94;

    private const string TemplateXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type CheckBox}">
          <Grid Background="Transparent">
            <Grid x:Name="TaskCheckIndicator"
                  Width="18"
                  Height="18"
                  HorizontalAlignment="Left"
                  VerticalAlignment="Bottom"
                  Background="{DynamicResource SurfacePrimaryBrush}">
              <Grid x:Name="TaskCheckMotionSurface"
                    RenderTransformOrigin="0.5,0.5">
                <Border x:Name="TaskCheckFill"
                        Background="{DynamicResource AccentPrimaryBrush}"
                        CornerRadius="4.5"
                        Opacity="0"
                        RenderTransformOrigin="0.5,0.5">
                  <Border.RenderTransform>
                    <ScaleTransform ScaleX="0.65" ScaleY="0.65" />
                  </Border.RenderTransform>
                </Border>
                <Border x:Name="TaskCheckHoverLayer"
                        CornerRadius="4.5"
                        IsHitTestVisible="False"
                        Opacity="0">
                  <Border.Style>
                    <Style TargetType="Border">
                      <Setter Property="Background" Value="{DynamicResource BgHoverBrush}" />
                      <Style.Triggers>
                        <DataTrigger Binding="{Binding IsChecked, RelativeSource={RelativeSource TemplatedParent}}" Value="True">
                          <Setter Property="Background" Value="{DynamicResource AccentHoverBrush}" />
                        </DataTrigger>
                        <DataTrigger Binding="{Binding IsChecked, RelativeSource={RelativeSource TemplatedParent}}" Value="{x:Null}">
                          <Setter Property="Background" Value="{DynamicResource AccentHoverBrush}" />
                        </DataTrigger>
                      </Style.Triggers>
                    </Style>
                  </Border.Style>
                </Border>
                <Border x:Name="TaskCheckPressedLayer"
                        CornerRadius="4.5"
                        IsHitTestVisible="False"
                        Opacity="0">
                  <Border.Style>
                    <Style TargetType="Border">
                      <Setter Property="Background" Value="{DynamicResource TransparentBrush}" />
                      <Style.Triggers>
                        <DataTrigger Binding="{Binding IsChecked, RelativeSource={RelativeSource TemplatedParent}}" Value="True">
                          <Setter Property="Background" Value="{DynamicResource AccentHoverBrush}" />
                        </DataTrigger>
                        <DataTrigger Binding="{Binding IsChecked, RelativeSource={RelativeSource TemplatedParent}}" Value="{x:Null}">
                          <Setter Property="Background" Value="{DynamicResource AccentHoverBrush}" />
                        </DataTrigger>
                      </Style.Triggers>
                    </Style>
                  </Border.Style>
                </Border>
                <Path x:Name="TaskCheckMark"
                      Width="12"
                      Height="12"
                      HorizontalAlignment="Center"
                      VerticalAlignment="Center"
                      Stretch="Uniform"
                      Stroke="{DynamicResource TextOnAccentBrush}"
                      StrokeThickness="1.9"
                      StrokeStartLineCap="Round"
                      StrokeEndLineCap="Round"
                      StrokeLineJoin="Round"
                      IsHitTestVisible="False"
                      Opacity="0"
                      RenderTransformOrigin="0.5,0.5">
                  <Path.RenderTransform>
                    <ScaleTransform ScaleX="0.65" ScaleY="0.65" />
                  </Path.RenderTransform>
                  <Path.Style>
                    <Style TargetType="Path">
                      <Setter Property="Data" Value="M 1,7 L 4.8,10.8 L 11,3.6" />
                      <Style.Triggers>
                        <DataTrigger Binding="{Binding IsChecked, RelativeSource={RelativeSource TemplatedParent}}" Value="{x:Null}">
                          <Setter Property="Data" Value="M 1,11 L 11,11" />
                        </DataTrigger>
                      </Style.Triggers>
                    </Style>
                  </Path.Style>
                </Path>
              </Grid>
              <Grid x:Name="TaskCheckPressPreview"
                    IsHitTestVisible="False"
                    Opacity="0"
                    RenderTransformOrigin="0.5,0.5">
                  <Grid.RenderTransform>
                  <ScaleTransform ScaleX="0" ScaleY="0" />
                </Grid.RenderTransform>
                <Border Background="{DynamicResource AccentPrimaryBrush}"
                        CornerRadius="4.5" />
                <Path Width="12"
                      Height="12"
                      HorizontalAlignment="Center"
                      VerticalAlignment="Center"
                      Data="M 1,7 L 4.8,10.8 L 11,3.6"
                      Stretch="Uniform"
                      Stroke="{DynamicResource TextOnAccentBrush}"
                      StrokeThickness="1.9"
                      StrokeStartLineCap="Round"
                      StrokeEndLineCap="Round"
                      StrokeLineJoin="Round"
                      IsHitTestVisible="False" />
              </Grid>
              <Border x:Name="TaskCheckBox"
                      Background="Transparent"
                      CornerRadius="4.5"
                      BorderThickness="1"
                      IsHitTestVisible="False">
                <Border.Style>
                  <Style TargetType="Border">
                    <Setter Property="BorderBrush" Value="{DynamicResource BorderDefaultBrush}" />
                    <Style.Triggers>
                      <DataTrigger Binding="{Binding IsChecked, RelativeSource={RelativeSource TemplatedParent}}" Value="True">
                        <Setter Property="BorderBrush" Value="{DynamicResource AccentPrimaryBrush}" />
                      </DataTrigger>
                      <DataTrigger Binding="{Binding IsChecked, RelativeSource={RelativeSource TemplatedParent}}" Value="{x:Null}">
                        <Setter Property="BorderBrush" Value="{DynamicResource AccentPrimaryBrush}" />
                      </DataTrigger>
                    </Style.Triggers>
                  </Style>
                </Border.Style>
              </Border>
            </Grid>
          </Grid>
        </ControlTemplate>
        """;

    private static readonly ControlTemplate Template =
        (XamlReader.Parse(TemplateXaml) as ControlTemplate)
        ?? throw new InvalidOperationException("Failed to build the task check box template.");

    public static CheckBox Create(double? lineHeight = null, double? baseline = null)
    {
        // AvalonEdit arranges inline elements at the top of each text line and
        // does not apply VerticalAlignment to that arrange slot. Give the
        // checkbox the line's full height and expose the text baseline so the
        // visible 18px indicator can be anchored to the line bottom.
        double height = Math.Max(18, lineHeight.GetValueOrDefault(18));
        CheckBox checkBox = new()
        {
            Width = 18,
            Height = height,
            Margin = new Thickness(1, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Focusable = true,
            Template = Template
        };
        TextBlock.SetBaselineOffset(
            checkBox,
            Math.Clamp(baseline.GetValueOrDefault(height), 0, height));
        InteractionAnimations.SetProfile(checkBox, InteractionAnimationProfile.Button);
        InteractionAnimations.SetTargetName(checkBox, "TaskCheckMotionSurface");
        InteractionAnimations.SetSurfaceHoverLayerName(checkBox, "TaskCheckHoverLayer");
        InteractionAnimations.SetSurfacePressedLayerName(checkBox, "TaskCheckPressedLayer");
        InteractionAnimations.SetHoverScale(checkBox, 1);
        UpdatePressedScale(checkBox);
        checkBox.Checked += OnCheckedStateChanged;
        checkBox.Unchecked += OnCheckedStateChanged;
        checkBox.Indeterminate += OnCheckedStateChanged;
        _ = new CheckBoxVisualAnimator(
            checkBox,
            "TaskCheckFill",
            "TaskCheckMark",
            "TaskCheckPressPreview");
        return checkBox;
    }

    private static void OnCheckedStateChanged(object sender, RoutedEventArgs e) =>
        UpdatePressedScale((CheckBox)sender);

    private static void UpdatePressedScale(CheckBox checkBox) =>
        InteractionAnimations.SetPressedScale(
            checkBox,
            checkBox.IsChecked == true ? CheckedPressedScale : DefaultPressedScale);
}
