namespace PdfUtil;

/// <summary>Base class of all exceptions thrown by PdfUtil. Messages never contain passwords or keys.</summary>
public class PdfUtilException : Exception
{
    /// <summary>Creates a new exception.</summary>
    public PdfUtilException(string message) : base(message) { }

    /// <summary>Creates a new exception wrapping an inner exception.</summary>
    public PdfUtilException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The supplied password does not authenticate against the document.</summary>
public sealed class PdfInvalidPasswordException : PdfUtilException
{
    /// <summary>Creates a new exception.</summary>
    public PdfInvalidPasswordException(string message) : base(message) { }
}

/// <summary>The input is not a PDF, or is damaged beyond what the reader can repair.</summary>
public class PdfCorruptException : PdfUtilException
{
    /// <summary>Creates a new exception.</summary>
    public PdfCorruptException(string message) : base(message) { }

    /// <summary>Creates a new exception wrapping an inner exception.</summary>
    public PdfCorruptException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>A configured hostile-input limit (size, nesting, object count, decompressed size) was exceeded.</summary>
public sealed class PdfLimitExceededException : PdfCorruptException
{
    /// <summary>Creates a new exception.</summary>
    public PdfLimitExceededException(string message) : base(message) { }
}

/// <summary>The document uses a security handler or encryption revision that PdfUtil does not support (for example public-key security).</summary>
public sealed class PdfUnsupportedEncryptionException : PdfUtilException
{
    /// <summary>Creates a new exception.</summary>
    public PdfUnsupportedEncryptionException(string message) : base(message) { }
}

/// <summary>The document is already encrypted and <see cref="AlreadyEncryptedPolicy.Throw"/> is in effect.</summary>
public sealed class PdfAlreadyEncryptedException : PdfUtilException
{
    /// <summary>Creates a new exception.</summary>
    public PdfAlreadyEncryptedException(string message) : base(message) { }
}

/// <summary>The operation needs an encrypted document, but the input is not encrypted.</summary>
public sealed class PdfNotEncryptedException : PdfUtilException
{
    /// <summary>Creates a new exception.</summary>
    public PdfNotEncryptedException(string message) : base(message) { }
}

/// <summary>The operation would violate a declared conformance level (for example encrypting a PDF/A file).</summary>
public sealed class PdfComplianceException : PdfUtilException
{
    /// <summary>Creates a new exception.</summary>
    public PdfComplianceException(string message) : base(message) { }
}

/// <summary>A password cannot be encoded for the selected algorithm (for example non-PDFDocEncoding characters for RC4/AES-128).</summary>
public sealed class PdfPasswordEncodingException : PdfUtilException
{
    /// <summary>Creates a new exception.</summary>
    public PdfPasswordEncodingException(string message) : base(message) { }
}

/// <summary>
/// The protected output failed the mandatory post-protection self-verification. No output was produced.
/// </summary>
public sealed class PdfVerificationException : PdfUtilException
{
    /// <summary>Creates a new exception.</summary>
    public PdfVerificationException(string message) : base(message) { }

    /// <summary>Creates a new exception wrapping an inner exception.</summary>
    public PdfVerificationException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The document has no attachment with the requested name.</summary>
public sealed class PdfAttachmentNotFoundException : PdfUtilException
{
    /// <summary>Creates a new exception.</summary>
    public PdfAttachmentNotFoundException(string message) : base(message) { }
}
