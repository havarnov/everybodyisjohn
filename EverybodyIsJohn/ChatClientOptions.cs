using System;
using System.ComponentModel.DataAnnotations;

public class ChatClientOptions
{
    [Required]
    public required string Model { get; init; }

    [Required]
    public required string ApiKey { get; init; }

    [Required]
    public required Uri Endpoint { get; init; }
}