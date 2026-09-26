namespace Wobkey.Crush80;

/// <summary>A 24-bit RGB color with one byte per channel.</summary>
/// <param name="Red">The red channel.</param>
/// <param name="Green">The green channel.</param>
/// <param name="Blue">The blue channel.</param>
public readonly record struct Rgb24(byte Red, byte Green, byte Blue);
