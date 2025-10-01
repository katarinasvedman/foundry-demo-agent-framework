@description('Logic App Name')
param logicAppName string = 'AgentTrigger-${uniqueString(resourceGroup().id)}'
param location string = resourceGroup().location
@description('Agent endpoint URL to POST to')
// no parameters required here; the workflow will be configured in the Logic Apps designer using the AI Foundry connector

// Consumption Logic App workflow resource (no App Service plan required)

// Deploy the workflow definition
resource workflow 'Microsoft.Logic/workflows@2019-05-01' = {
  name: logicAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    definition: json(loadTextContent('agenttrigger-workflow.json'))
    parameters: {}
  }
}

// Output the managed identity principal and workflow resource id
output workflowResourceId string = workflow.id
output managedIdentityPrincipalId string = workflow.identity.principalId
