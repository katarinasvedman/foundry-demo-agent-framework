@description('Name of the App Service')
param appName string = 'foundry-agents-${uniqueString(resourceGroup().id)}'
param location string = resourceGroup().location

resource plan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: '${appName}-plan'
  location: location
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  properties: {
    reserved: true
  }
}

resource app 'Microsoft.Web/sites@2022-03-01' = {
  name: appName
  location: location
  properties: {
    serverFarmId: plan.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
    }
    
  }
}

output siteUri string = 'https://${app.properties.defaultHostName}'

// Module: AgentTrigger Logic App
module agentTrigger './agenttrigger.bicep' = {
  name: 'deployAgentTrigger'
  params: {
    logicAppName: 'AgentTrigger'
    location: location
  }
}

output agentTriggerWorkflowId string = agentTrigger.outputs.workflowResourceId
