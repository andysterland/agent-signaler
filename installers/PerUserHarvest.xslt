<?xml version="1.0" encoding="utf-8"?>
<xsl:stylesheet version="1.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:wix="http://wixtoolset.org/schemas/v4/wxs"
                xmlns="http://wixtoolset.org/schemas/v4/wxs"
                exclude-result-prefixes="wix">
  <xsl:output method="xml" indent="yes" />
  <xsl:template match="@*|node()">
    <xsl:copy><xsl:apply-templates select="@*|node()" /></xsl:copy>
  </xsl:template>
  <!-- Per-user components need HKCU key paths, not files beneath the user profile. -->
  <xsl:template match="wix:File/@KeyPath" />
  <xsl:template match="wix:Component">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
      <RegistryValue Root="HKCU"
                     Key="Software\AgentSignaler\Installer\$(var.InstallerProduct)\Components"
                     Name="{@Id}" Type="integer" Value="1" KeyPath="yes" />
    </xsl:copy>
  </xsl:template>
  <xsl:template match="wix:Directory">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
      <Component Id="Cleanup_{@Id}" Guid="*">
        <RemoveFolder Id="Remove_{@Id}" On="uninstall" />
        <RegistryValue Root="HKCU"
                       Key="Software\AgentSignaler\Installer\$(var.InstallerProduct)\Directories"
                       Name="{@Id}" Type="integer" Value="1" KeyPath="yes" />
      </Component>
    </xsl:copy>
  </xsl:template>
  <xsl:template match="wix:ComponentGroup">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
      <xsl:for-each select="//wix:Directory">
        <ComponentRef Id="Cleanup_{@Id}" />
      </xsl:for-each>
    </xsl:copy>
  </xsl:template>
</xsl:stylesheet>
