param appName string = 'videotranslator-tinumbraco'
param tenantId string = 'dc74cf4c-f60c-409a-b4de-69db311bfb6f'
param allowedUserObjectId string = '987cbe21-d0a0-4eb7-a91a-e670f6eb4438'
@allowed([ 'MicrosoftEntra', 'SingleAccount' ])
param authenticationMode string = 'MicrosoftEntra'
param singleAccountUsername string = ''
@secure()
param singleAccountPasswordHash string = ''
param entraClientId string = ''
@secure()
param entraClientSecret string = ''
param translationEnabled bool = false
param processingEnabled bool = false
param speechEnabled bool = false

resource plan 'Microsoft.Web/serverfarms@2024-04-01' existing = {
  name: 'tinumbraco-plan'
}
module integration 'translation-subnet.bicep' = {
  name: 'videotranslator-integration'
  scope: resourceGroup('rg-codymo-lease-dev')
}
resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: 'newzealandnorth'
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    virtualNetworkSubnetId: integration.outputs.subnetId
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      numberOfWorkers: 1
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      appCommandLine: 'bash /home/site/wwwroot/startup.sh'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
        { name: 'AzureHosting__Enabled', value: 'true' }
        { name: 'AzureHosting__AuthenticationMode', value: authenticationMode }
        { name: 'AzureHosting__ProcessingEnabled', value: string(processingEnabled) }
        { name: 'AzureHosting__TenantId', value: tenantId }
        { name: 'AzureHosting__AllowedUserObjectId', value: allowedUserObjectId }
        { name: 'SingleAccountLogin__Username', value: singleAccountUsername }
        { name: 'SingleAccountLogin__PasswordHash', value: singleAccountPasswordHash }
        { name: 'SingleAccountLogin__KeyDirectory', value: '/home/videotranslator-auth/keys' }
        { name: 'AzureOpenAI__Enabled', value: string(translationEnabled) }
        { name: 'AzureOpenAI__CredentialMode', value: 'ManagedIdentity' }
        { name: 'BlobStorage__CredentialMode', value: 'ManagedIdentity' }
        { name: 'AzureSpeech__Enabled', value: string(speechEnabled) }
        { name: 'AzureSpeech__SynthesisEnabled', value: string(speechEnabled) }
        { name: 'AzureSpeech__CredentialMode', value: 'ManagedIdentity' }
        { name: 'JobStorage__Provider', value: 'AzureBlob' }
        { name: 'Media__FFmpegPath', value: 'ffmpeg' }
        { name: 'Media__FFprobePath', value: 'ffprobe' }
        { name: 'Media__PollIntervalSeconds', value: '15' }
        { name: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET', value: entraClientSecret }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
      ]
    }
  }
}
resource auth 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: app
  name: 'authsettingsV2'
  properties: {
    platform: { enabled: authenticationMode == 'MicrosoftEntra', runtimeVersion: '~1' }
    globalValidation: authenticationMode == 'MicrosoftEntra' ? {
      requireAuthentication: true
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureActiveDirectory'
    } : {
      requireAuthentication: false
      unauthenticatedClientAction: 'AllowAnonymous'
    }
    httpSettings: { requireHttps: true }
    identityProviders: authenticationMode == 'MicrosoftEntra' ? {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: entraClientId
          clientSecretSettingName: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
          openIdIssuer: 'https://login.microsoftonline.com/${tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [ entraClientId, 'api://${entraClientId}' ]
          defaultAuthorizationPolicy: {
            allowedPrincipals: { identities: [ allowedUserObjectId ] }
          }
        }
      }
    } : {}
    login: { tokenStore: { enabled: false } }
  }
}
resource ftpPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: app
  name: 'ftp'
  properties: { allow: false }
}
resource scmPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: app
  name: 'scm'
  properties: { allow: false }
}
output principalId string = app.identity.principalId
output appUrl string = 'https://${app.properties.defaultHostName}'
