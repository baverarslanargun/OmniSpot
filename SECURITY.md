# Security

## Groq API credentials

OmniSpot reads Groq credentials from environment variables. Never commit real API keys.

Use one shared key:

```powershell
$env:OMNISPOT_GROQ_API_KEY = "<your-key>"
```

Or configure separate keys for the two request paths:

```powershell
$env:OMNISPOT_GROQ_INTENT_API_KEY = "<your-intent-key>"
$env:OMNISPOT_GROQ_KEYWORD_API_KEY = "<your-keyword-key>"
```

Any credential that has appeared in Git history must be revoked and replaced at its provider. Removing it from the latest source file does not remove it from earlier commits.

## Reporting a vulnerability

Do not open a public issue containing credentials or exploit details. Contact the repository owner privately first.
