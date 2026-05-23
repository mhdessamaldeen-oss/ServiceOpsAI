# SupportFlow AI - Development & Ops Skill

This file contains the "Expert Skills" for the Antigravity AI assistant to manage the SupportFlow AI Platform.

## 🚀 Skill: Launch Application
Use this skill when the USER asks to "Run the app" or after any code changes to ensure they are reflected in the UI.

1.  **Stop Phase**:
    *   If a background command is running the app, terminate it using `send_command_input`.

2.  **Build Phase**:
    *   Run `dotnet build` to compile all forensic and UI refactors.

2.  **Execution Phase**:
    *   Set Environment Variables:
        *   `ASPNETCORE_ENVIRONMENT=Development`
        *   `ASPNETCORE_URLS=https://localhost:7081;http://localhost:5081`
    *   Run: `dotnet run --no-build --urls "https://localhost:7081;http://localhost:5081"`
    *   **Port Enforcement**: Port **7081** must be used for forensic testing.

3.  **Access Phase**:
    *   **Super Admin Access**: Log in at `https://localhost:7081/Identity/Account/Login`
    *   **Username**: `admin@tech.local`
    *   **Password**: `Admin@123`

## 📊 Skill: Run Forensic Assessment

When the USER asks to "Run tests", "Start assessment", or "Verify cases":

1.  Launch the app using the **Launch Application** skill above.
2.  Direct the user to the **Forensic Assessment Lab** page:
    *   `https://localhost:7081/AiAnalysis/CopilotAssessment`

---

## 🤖 Skill: Manage Native Ollama Models

When the USER asks to "Add a model", "Download llama3", or "Manage local AI":

1.  **Native Requirement**: Ensure `ollama serve` is running natively on Windows (not Docker).
2.  **Pull Interface**: Use the Settings -> Ollama tab to type a model name (e.g. `llama3.2`) and click "Pull Model".
3.  **Forensic Metrics**:
    *   **Brain Badge**: Identifies which model (Ollama Native vs Docker) produced the assessment result.
    *   **Integrity Badge**: Indicates if data was truncated due to context window limits.

---
*Created by Antigravity AI - May 2026*
