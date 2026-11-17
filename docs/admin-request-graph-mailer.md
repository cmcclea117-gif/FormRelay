# Template: request for your Microsoft 365 admin (app-only mail sending)

Only needed if you choose `Email:Provider = Graph`. SMTP needs none of this.

Copy the message below into an email or chat after replacing the placeholders:

- `<APP-NAME>`: the name of the app registration (for example "FormRelay Mailer")
- `<APP-ID>`: its Application (client) ID, once it exists
- `<SERVER>`: the machine FormRelay runs on
- `<SENDER-MAILBOX>`: the full address of the mailbox all mail is sent from (a shared mailbox is fine)
- `<GROUP-ADDRESS>`: the mail-enabled security group the admin creates for that mailbox

Don't commit the filled-in version. Run `FormRelay-Setup.ps1 graph-cert` first so you have the `.cer` and the thumbprint to attach.

---

Hi,

We're setting up "<APP-NAME>" (app ID `<APP-ID>`) to send email as `<SENDER-MAILBOX>` with app-only access, so nobody has to sign in. Could you please:

1. **API permission.** In the app registration, under API permissions, add Microsoft Graph **Application** permission **Mail.Send**, and grant admin consent.

2. **Certificate.** Under Certificates & secrets, upload the attached public key (`.cer`). It was created on `<SERVER>` and its private key never left that machine, so nothing secret travels. The thumbprint is `<THUMBPRINT>`; please check it matches. It expires on `<EXPIRY>`; please set a renewal reminder.

3. **Restrict the app to one mailbox**, so it can't send as anyone else. Put `<SENDER-MAILBOX>` in a mail-enabled security group, then in Exchange Online PowerShell:

   ```powershell
   New-ApplicationAccessPolicy -AppId <APP-ID> -PolicyScopeGroupId <GROUP-ADDRESS> -AccessRight RestrictAccess -Description "FormRelay mailer: one mailbox only"
   Test-ApplicationAccessPolicy -Identity <SENDER-MAILBOX> -AppId <APP-ID>
   ```

   The test should report that access is granted. (RBAC for Applications is fine instead if you prefer it.) Without this policy, app-only Mail.Send can send as any mailbox in the tenant.

4. **Sender mailbox.** Please confirm `<SENDER-MAILBOX>` exists as a licensed or shared mailbox. It does not need to be able to sign in.

Policy changes can take up to about 30 minutes to take effect. The server needs outbound HTTPS (443) to `login.microsoftonline.com` and `graph.microsoft.com`. Thank you!

---

## After the admin replies

Add to `C:\ProgramData\FormRelay\config.json` and run `Restart-Service FormRelay`:

```json
"Email": { "Provider": "Graph" },
"Graph": { "TenantId": "<tenant-id>", "ClientId": "<APP-ID>", "Sender": "<SENDER-MAILBOX>", "CertificateThumbprint": "<THUMBPRINT>" }
```

Then run `FormRelay-Setup.ps1 check`. The Graph line shows the certificate expiry, or tells you the service account cannot read the private key.
